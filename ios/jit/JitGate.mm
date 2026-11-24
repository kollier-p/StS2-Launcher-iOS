#import <UIKit/UIKit.h>
#import <objc/runtime.h>
#import <CoreImage/CoreImage.h>
#include <dlfcn.h>
#include <unistd.h>

extern "C" int csops(pid_t, unsigned int, void *, size_t);
static BOOL (*originalLaunch)(id, SEL, UIApplication *, NSDictionary *);
static NSMutableDictionary<NSString *, NSValue *> *lifecycleMethods;
static BOOL engineStarted;

@interface STS2JitGate : NSObject
@property(nonatomic, strong) id service;
@property(nonatomic, strong) UIApplication *application;
@property(nonatomic, copy) NSDictionary *launchOptions;
@property(nonatomic, strong) UIWindow *window;
@property(nonatomic, strong) UILabel *notice;
@property(nonatomic, strong) UIImageView *qr;
@property(nonatomic, strong) UIButton *retry;
@property(nonatomic, assign) BOOL bootstrapStarted;
@property(nonatomic, assign) BOOL justInstalled;
@property(nonatomic, strong) NSTimer *timer;
@property(nonatomic, copy) NSString *documents;
- (void)start;
- (void)checkReadiness;
- (void)beginBootstrap;
@end

static STS2JitGate *startupGate;

static BOOL LaunchWhenReady(id service, SEL selector, UIApplication *application, NSDictionary *options) {
    startupGate = [STS2JitGate new];
    startupGate.service = service;
    startupGate.application = application;
    startupGate.launchOptions = options;
    [startupGate start];
    return YES;
}

static void ForwardLifecycle(id service, SEL selector, UIApplication *application) {
    // Godot's lifecycle callbacks dereference its OS singleton before checking it.
    if (!engineStarted) return;
    auto original = (void (*)(id, SEL, UIApplication *))lifecycleMethods[NSStringFromSelector(selector)].pointerValue;
    original(service, selector, application);
}

@implementation STS2JitGate
+ (void)load {
    // Main::setup loads the PCK during didFinishLaunching, before setupView runs.
    Class service = NSClassFromString(@"GDTAppDelegateService");
    Method launch = class_getInstanceMethod(service, @selector(application:didFinishLaunchingWithOptions:));
    if (!launch) abort();
    originalLaunch = (BOOL (*)(id, SEL, UIApplication *, NSDictionary *))method_setImplementation(launch, (IMP)LaunchWhenReady);
    lifecycleMethods = [NSMutableDictionary new];
    for (NSString *name in @[@"applicationDidBecomeActive:", @"applicationWillResignActive:",
             @"applicationDidEnterBackground:", @"applicationWillEnterForeground:",
             @"applicationDidReceiveMemoryWarning:", @"applicationWillTerminate:"]) {
        Method method = class_getInstanceMethod(service, NSSelectorFromString(name));
        if (!method) abort();
        lifecycleMethods[name] = [NSValue valueWithPointer:(const void *)method_setImplementation(method, (IMP)ForwardLifecycle)];
    }
}

- (void)start {
    self.documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
    UIViewController *controller = [UIViewController new];
    controller.view.backgroundColor = UIColor.systemBackgroundColor;
    self.notice = [UILabel new];
    self.notice.translatesAutoresizingMaskIntoConstraints = NO;
    self.notice.textColor = UIColor.labelColor;
    self.notice.font = [UIFont preferredFontForTextStyle:UIFontTextStyleTitle3];
    self.notice.textAlignment = NSTextAlignmentCenter;
    self.notice.numberOfLines = 0;
    UIScrollView *scroll = [UIScrollView new];
    scroll.translatesAutoresizingMaskIntoConstraints = NO;
    [controller.view addSubview:scroll];
    UIStackView *stack = [UIStackView new];
    stack.translatesAutoresizingMaskIntoConstraints = NO;
    stack.axis = UILayoutConstraintAxisVertical;
    stack.alignment = UIStackViewAlignmentCenter;
    stack.spacing = 12;
    [scroll addSubview:stack];
    [stack addArrangedSubview:self.notice];
    self.qr = [UIImageView new];
    self.qr.translatesAutoresizingMaskIntoConstraints = NO;
    self.qr.contentMode = UIViewContentModeScaleAspectFit;
    self.qr.hidden = YES;
    [stack addArrangedSubview:self.qr];
    self.retry = [UIButton buttonWithType:UIButtonTypeSystem];
    [self.retry setTitle:@"Retry Steam setup" forState:UIControlStateNormal];
    [self.retry addTarget:self action:@selector(beginBootstrap) forControlEvents:UIControlEventTouchUpInside];
    self.retry.hidden = YES;
    [stack addArrangedSubview:self.retry];
    UILayoutGuide *area = controller.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [scroll.topAnchor constraintEqualToAnchor:area.topAnchor],
        [scroll.bottomAnchor constraintEqualToAnchor:area.bottomAnchor],
        [scroll.leadingAnchor constraintEqualToAnchor:area.leadingAnchor],
        [scroll.trailingAnchor constraintEqualToAnchor:area.trailingAnchor],
        [stack.topAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.topAnchor constant:18],
        [stack.bottomAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.bottomAnchor constant:-18],
        [stack.leadingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.leadingAnchor],
        [stack.trailingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.trailingAnchor],
        [stack.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor],
        [self.notice.widthAnchor constraintEqualToAnchor:stack.widthAnchor constant:-48],
        [self.qr.widthAnchor constraintEqualToConstant:220],
        [self.qr.heightAnchor constraintEqualToConstant:220],
    ]];
    self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = controller;
    [self.service setValue:self.window forKey:@"window"];
    [self.window makeKeyAndVisible];

    NSError *error = nil;
    NSFileManager *files = NSFileManager.defaultManager;
    NSString *mods = [self.documents stringByAppendingPathComponent:@"mods"];
    NSString *readme = [self.documents stringByAppendingPathComponent:@"SETUP.txt"];
    NSString *instructions = @"StS2 JIT\n\nCopy the prepared StS2.pck into this folder.\n"
        @"Copy mod folders into mods. Keep their original filenames.\n"
        @"To restore saves, copy default and mod_configs here as well.\n\n"
        @"Return to StS2 JIT and enable JIT for it using your JIT tool.\n"
        @"JIT must be enabled again whenever the app process restarts.\n";
    if (![files createDirectoryAtPath:mods withIntermediateDirectories:YES attributes:nil error:&error] ||
        (![files fileExistsAtPath:readme] && ![instructions writeToFile:readme atomically:YES encoding:NSUTF8StringEncoding error:&error])) {
        self.notice.text = [@"StS2 JIT\n\nCould not prepare the Files folder.\n" stringByAppendingString:error.localizedDescription];
        return;
    }

    __weak STS2JitGate *gate = self;
    self.timer = [NSTimer scheduledTimerWithTimeInterval:0.5 repeats:YES block:^(NSTimer *timer) {
        [gate checkReadiness];
    }];
    self.notice.text = @"StS2 JIT\n\nPreparing...";
}

- (void)checkReadiness {
    if (self.application.applicationState != UIApplicationStateActive) return;
    NSString *pack = [self.documents stringByAppendingPathComponent:@"StS2.pck"];
    NSDictionary *attributes = [NSFileManager.defaultManager attributesOfItemAtPath:pack error:nil];
    unsigned long long expectedSize = [NSBundle.mainBundle.infoDictionary[@"STS2ContentSize"] unsignedLongLongValue];
    NSString *readyPath = [self.documents stringByAppendingPathComponent:@"StS2.ready.json"];
    NSData *readyData = [NSData dataWithContentsOfFile:readyPath];
    NSDictionary *ready = readyData ? [NSJSONSerialization JSONObjectWithData:readyData options:0 error:nil] : nil;
    if ([ready isKindOfClass:NSDictionary.class]) {
        unsigned long long bytes = [ready[@"PackBytes"] unsignedLongLongValue];
        unsigned long long manifest = [ready[@"Manifest"] unsignedLongLongValue];
        NSString *managed = [self.documents stringByAppendingPathComponent:
            [NSString stringWithFormat:@"steam-game-installs/%llu/Managed/sts2.dll", manifest]];
        // A corrupt/partial ready marker never satisfies this gate.
        expectedSize = bytes > 0 && manifest > 0 &&
            [NSFileManager.defaultManager fileExistsAtPath:managed] ? bytes : 0;
    }
    BOOL complete = [[attributes fileType] isEqualToString:NSFileTypeRegular] &&
        expectedSize > 0 && [attributes fileSize] == expectedSize;
    if (self.justInstalled) return;
    if (!complete) {
        uint32_t permission = 0;
        if (csops(getpid(), 0, &permission, sizeof(permission)) != 0 || !(permission & 0x10000000)) {
            self.notice.text = @"StS2 JIT\n\nNo game is installed yet.\n\n"
                @"Enable JIT for this app with your JIT tool or a Mac debugger. "
                @"After JIT is enabled, SteamKit2 will sign you in, download your owned game, "
                @"prepare it for iOS and install it here.\n\n"
                @"JIT must be enabled again whenever you restart the app.";
            return;
        }
        if (!self.bootstrapStarted) [self beginBootstrap];
        return;
    }
    uint32_t flags = 0;
    if (csops(getpid(), 0, &flags, sizeof(flags)) != 0 || !(flags & 0x10000000)) {
        self.notice.text = @"StS2 JIT\n\nGame content is ready.\n\n"
            @"Enable JIT for this app with your JIT tool or a Mac debugger.\n"
            @"The game will start automatically when JIT is ready.\n\n"
            @"JIT must be enabled again after the app process restarts.";
        return;
    }
    [self.timer invalidate];
    self.timer = nil;
    self.notice.text = @"StS2 JIT\n\nStarting game...";
    originalLaunch(self.service, @selector(application:didFinishLaunchingWithOptions:), self.application, self.launchOptions);
    engineStarted = YES;
    // The initial active notification was deliberately withheld while waiting.
    ForwardLifecycle(self.service, @selector(applicationDidBecomeActive:), self.application);
    self.window.hidden = YES;
    startupGate = nil;
}
@end
