using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using VF.Utils;

namespace VF {
    internal static class BadInstallDetector {
        [VFInit]
        private static void Init() {
            var packageInfo = PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());
            if (packageInfo == null || packageInfo.name != "com.vrcfury.vrcfury") {
                return;
            }

            var packagePath = packageInfo.assetPath;
            var isLocalPackage = Directory.Exists(packagePath) &&
                                 Path.GetFullPath(packagePath).StartsWith(Path.GetFullPath("Packages"));
            var manifestPath = "Packages/manifest.json";
            var manifestContainsVrcfury = File.Exists(manifestPath) && File.ReadLines(manifestPath)
                .Any(line => line.Contains(packageInfo.name));

            if (isLocalPackage && manifestContainsVrcfury) {
                DialogUtils.DisplayDialog(
                    "VRCFury",
                    "The VRCFury install is partially corrupt. The updater may have broken, or you may have updated " +
                    "from an old manual install to a new version using the VCC.\n" +
                    "\n" +
                    "Please download and import " +
                    "https://vrcfury.com/installer to resolve this issue.",
                    "Ok"
                );
            }
        }
    }
}
