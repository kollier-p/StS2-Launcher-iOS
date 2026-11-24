// Compile-only API declarations for the original game's mod manager. The
// downloaded, Steam-owned sts2.dll replaces this assembly on the iPhone.
// Signatures must be checked against supported game builds before Play.
using Godot;

namespace MegaCrit.Sts2.Core.Modding
{
    public class ModManager
    {
        public void Initialize(IModManagerFileIo fileIo, ModSettings settings) => throw null;
    }

    public class ModSettings { }

    public interface IModManagerFileIo
    {
        bool DirectoryExists(string path);
        string[] GetDirectoriesAt(string path);
        string[] GetFilesAt(string path);
        bool FileExists(string path);
        Stream OpenStream(string path, FileAccess.ModeFlags mode);
        void MakeDirRecursive(string path);
        Error CopyFile(string sourcePath, string destinationPath);
    }
}
