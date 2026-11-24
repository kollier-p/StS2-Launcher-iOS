#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <CoreImage/CoreImage.h>
#include <dlfcn.h>

// Included after JitGate.mm in the Godot-generated Objective-C++ host.
// We reuse its UIKit startup screen rather than letting Godot load a missing
// pack. The SteamKit2 and preparation code runs in Mono with the JIT enabled.
@interface STS2JitGate (SteamSetup)
- (void)beginBootstrap;
@end

@implementation STS2JitGate (SteamSetup)
- (void)beginBootstrap {
    if (self.application.applicationState != UIApplicationStateActive || self.justInstalled) return;
    self.bootstrapStarted = YES;
    self.retry.hidden = YES;
    self.qr.hidden = YES;
    self.notice.text = @"Starting SteamKit2 installer...";
    setenv("STS2_DOCUMENTS", self.documents.UTF8String, 1);
    NSString *framework = [NSBundle.mainBundle.privateFrameworksPath
        stringByAppendingPathComponent:@"sts2.framework/sts2"];
    void *handle = dlopen(framework.fileSystemRepresentation, RTLD_NOW | RTLD_GLOBAL);
    if (!handle) {
        self.notice.text = @"Mono JIT bootstrap framework is missing. Rebuild the app.";
        self.retry.hidden = NO;
        return;
    }
    auto begin = (int (*)())dlsym(handle, "sts2_bootstrap_start");
    if (!begin || begin() != 0) {
        self.notice.text = @"Could not start the standalone Steam installer. Verify JIT and try again.";
        self.retry.hidden = NO;
    }
}
@end

// Called from the .NET background thread: never update UIKit except on main.
extern "C" __attribute__((visibility("default"), used))
void sts2_launcher_status(const char *utf8) {
    NSString *text = utf8 ? [NSString stringWithUTF8String:utf8] : @"";
    if (!text || text.length > 4096) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!startupGate || startupGate.justInstalled) return;
        startupGate.notice.text = text;
        NSString *lower = text.lowercaseString;
        startupGate.retry.hidden = !([lower containsString:@"stopped"] ||
            [lower containsString:@"failed"] || [lower containsString:@"could not"] ||
            [lower containsString:@"did not"] || [lower containsString:@"not supported"]);
    });
}

extern "C" __attribute__((visibility("default"), used))
void sts2_launcher_qr(const char *utf8) {
    NSString *text = utf8 ? [NSString stringWithUTF8String:utf8] : @"";
    if (!text || text.length > 4096) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!startupGate || startupGate.justInstalled) return;
        if (text.length == 0) {
            startupGate.qr.hidden = YES;
            return;
        }
        NSURLComponents *uri = [NSURLComponents componentsWithString:text];
        if (![uri.scheme isEqualToString:@"https"] || ![uri.host isEqualToString:@"s.team"]
            || uri.user.length != 0 || uri.password.length != 0) return;
        CIFilter *qr = [CIFilter filterWithName:@"CIQRCodeGenerator"];
        [qr setValue:[text dataUsingEncoding:NSUTF8StringEncoding] forKey:@"inputMessage"];
        [qr setValue:@"M" forKey:@"inputCorrectionLevel"];
        CIImage *image = [qr.outputImage imageByApplyingTransform:CGAffineTransformMakeScale(6, 6)];
        CIContext *context = [CIContext contextWithOptions:nil];
        CGImageRef result = image ? [context createCGImage:image fromRect:image.extent] : nullptr;
        if (!result) return;
        startupGate.qr.image = [UIImage imageWithCGImage:result];
        startupGate.qr.hidden = NO;
        CGImageRelease(result);
    });
}

extern "C" __attribute__((visibility("default"), used))
void sts2_launcher_ready() {
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!startupGate) return;
        startupGate.justInstalled = YES;
        startupGate.qr.hidden = YES;
        startupGate.retry.hidden = YES;
        startupGate.notice.text = @"Slay the Spire 2 is installed.\n\n"
            @"Close this app fully and reopen it. Enable JIT again to boot the game.\n\n"
            @"The game's mod loader will use the patched Mono JIT and Harmony at runtime.";
    });
}
