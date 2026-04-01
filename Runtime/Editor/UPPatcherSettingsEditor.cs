#if UNITY_EDITOR && UNITY_2020_3_OR_NEWER
using System;
using System.IO;
using EditorAttributes;
using Nomnom.UnityProjectPatcher.UnityPackages;
using UnityEngine;

namespace Nomnom.UnityProjectPatcher {
    public partial class UPPatcherSettings {
        [Button]
        public void OpenGameVersionUrl() {
            var gameVersion = Path.GetFileNameWithoutExtension(GameVersion);
            Application.OpenURL($"https://unity.com/releases/editor/whats-new/{gameVersion}");
        }
        
        [Button]
        public void GetGamePackages() {
            if (GameManagedPath is null) {
                Debug.LogError("Game Managed Path is null");
                return;
            }
            
            if (!Directory.Exists(GameManagedPath)) {
                Debug.LogError("Game Managed Path does not exist");
                return;
            }
            
            _exactPackagesFound.Clear();
            _dllsToCopy.Clear();

            foreach (var package in PackagesUtility.GetGamePackages(this)) {
                switch (package.matchType) {
                    case PackageMatchType.Package:
                        _exactPackagesFound.Add(package);
                        break;
                    case PackageMatchType.DLL:
                        _dllsToCopy.Add(new FolderMapping("", package.name, ""));
                        break;
                }
            }
            
            PatcherUtility.SetDirty(this);
        }
    }
}
#endif