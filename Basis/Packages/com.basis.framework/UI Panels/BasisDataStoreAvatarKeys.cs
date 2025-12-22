using BasisSerializer.OdinSerializer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    public static class BasisDataStoreAvatarKeys
    {
        [System.Serializable]
        public class AvatarKey
        {
            public string Url;
            public string Pass;
        }
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
        private static string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Application.companyName, Application.productName);
        public static string FilePath = Path.Combine(directory, "KeyStore.json");
#else
        public static string FilePath = Path.Combine(Application.persistentDataPath, "KeyStore.json");
        
#endif
        [SerializeField]
        private static List<AvatarKey> keys = new List<AvatarKey>();

        public static async Task AddNewKey(AvatarKey newKey)
        {
            if (keys.Contains(newKey) == false)
            {
                keys.Add(newKey);
                await SaveKeysToFile();
                BasisDebug.Log($"Key added: {newKey.Url}");
            }
        }

        public static async Task RemoveKey(AvatarKey keyToRemove)
        {
            var key = keys.Find(k => k.Url == keyToRemove.Url && k.Pass == keyToRemove.Pass);
            if (key != null)
            {
                keys.Remove(key);
                await SaveKeysToFile();
                BasisDebug.Log($"Key removed: {keyToRemove.Url}");
            }
            else
            {
                BasisDebug.Log("Key not found.");
            }
        }

        public static async Task LoadKeys()
        {
            BasisDebug.Log($"Loading keys from file at path: {FilePath}");
            if (File.Exists(FilePath))
            {
                try
                {
                    byte[] byteData = await File.ReadAllBytesAsync(FilePath);
                    keys = SerializationUtility.DeserializeValue<List<AvatarKey>>(byteData, DataFormat.Binary);
                    BasisDebug.Log("Keys loaded successfully. Count: " + keys.Count);
                }
                catch (System.Exception e)
                {
                    BasisDebug.LogError($"Failed to load keys: {e.Message}");
                }
            }
            else
            {
                BasisDebug.Log("No key file found. Starting fresh.");
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string checkMigration = Path.Combine(Application.persistentDataPath, "KeyStore.json");
                if (File.Exists(checkMigration))
                {
                    File.Copy(checkMigration, FilePath);
                }
#endif
            }
        }

        private static async Task SaveKeysToFile()
        {
            try
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
#endif
                byte[] byteData = SerializationUtility.SerializeValue<List<AvatarKey>>(keys, DataFormat.Binary);
                await File.WriteAllBytesAsync(FilePath, byteData);
                BasisDebug.Log($"Keys saved to file at: {FilePath}");
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to save keys: {e.Message}");
            }
        }

        public static List<AvatarKey> DisplayKeys()
        {
            return keys;
        }
    }
}
