#if UNITY_2020_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.PackageManager;
using UnityEngine.Pool;

namespace Nomnom.UnityProjectPatcher.UnityPackages {
    public static class PackagesUtility {
#if UNITY_EDITOR
        public static IEnumerable<FoundDllInfo> GetGamePackages(UPPatcherSettings settings) {
            var ignoredPrefixes = settings.IgnoredDllPrefixes;
            var files = Directory.EnumerateFiles(settings.GameDataPath!, "*.dll", SearchOption.AllDirectories).ToArray();

            var stringToMatch = @"\Library\PackageCache\";
            var bytesToMatch = Encoding.UTF8.GetBytes(stringToMatch);

            var matchEnd = (byte)'\\';

            var triedIDs = new HashSet<string>();
            var foundPackages = new Dictionary<string, PackageInfo>();

            for (var i = 0; i < files.Length; i++) {
                var file = files[i];
                UnityEditor.EditorUtility.DisplayProgressBar("Grabbing Packages", $"Checking {file}...", i / (float)files.Length);

                var match = 0;

                while (true) {
                    var dllData = File.ReadAllBytes(file);
                    match = IndexOfSequence(match, dllData, bytesToMatch);

                    if (match == -1) {
                        var relativePath = Path.GetRelativePath(settings.GameManagedPath!, file);
                        if (ignoredPrefixes.Any(relativePath.StartsWith))
                            break;
                        yield return new FoundDllInfo(relativePath);
                        break;
                    }

                    ListPool<byte>.Get(out var packageNameBytes);

                    match += bytesToMatch.Length;
                    while (dllData[match] != matchEnd)
                        packageNameBytes.Add(dllData[match++]);
                    var packageID = Encoding.UTF8.GetString(packageNameBytes.ToArray());

                    if (triedIDs.Contains(packageID))
                        continue;
                    triedIDs.Add(packageID);

                    var singlePackageRequest = Client.Search(packageID);
                    while (!singlePackageRequest.IsCompleted) ;
                    if (singlePackageRequest.Result == null || singlePackageRequest.Result.Length != 1)
                        continue;
                    var package = singlePackageRequest.Result[0];
                    if (package == null)
                        continue;

                    if (foundPackages.ContainsKey(package.name))
                        continue;

                    foreach (var dependency in package.dependencies)
                        foundPackages.Remove(dependency.name);

                    foundPackages[package.name] = package;
                    break;
                }
            }

            foreach (var (name, package) in foundPackages.OrderBy(kvp => kvp.Key))
                yield return new FoundDllInfo(package, PackageMatchType.Package);

            UnityEditor.EditorUtility.ClearProgressBar();
        }

        private static int IndexOfSequence(int start, byte[] content, byte[] sequence) {
            if (start < 0)
                throw new IndexOutOfRangeException("start < 0");
            if (content.Length < sequence.Length)
                return -1;

            var lastPossibleMatch = content.Length - sequence.Length;
            for (var i = start; i <= lastPossibleMatch; i++) {
                var isMatch = true;

                for (var j = 0; j < sequence.Length; j++) {
                    var a = content[i + j];
                    var b = sequence[j];
                    if (a != b)
                        isMatch = false;
                }

                if (isMatch)
                    return i;
            }

            return -1;
        }
#endif
    }
}
#endif