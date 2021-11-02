using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BuildToolUtilities;

namespace BuildTool
{
	// Helper functions for dealing with encryption and pak signing
	public static class EncryptionAndSigning
	{
		// Wrapper class for a single RSA key
		public class SigningKey
		{
			public byte[] Exponent;
			public byte[] Modulus;

			public bool IsValid() 
				=> Exponent != null && Modulus != null && 0 < Exponent.Length && 0 < Modulus.Length;
		}

		// Wrapper class for an RSA public/private key pair
		public class SigningKeyPair
		{
			public SigningKey PublicKey = new SigningKey();
			public SigningKey PrivateKey = new SigningKey();

			public bool IsValid() 
				=> PublicKey != null && PrivateKey != null && PublicKey.IsValid() && PrivateKey.IsValid();


			// Returns TRUE if this is a short key from the old 256-bit system

			public bool IsUnsecureLegacyKey()
			{
				int LongestKey = PublicKey.Exponent.Length;
				LongestKey     = Math.Max(LongestKey, PublicKey.Modulus.Length);
				LongestKey     = Math.Max(LongestKey, PrivateKey.Exponent.Length);
				LongestKey     = Math.Max(LongestKey, PrivateKey.Modulus.Length);

				return LongestKey <= 64;
			}
		}

		// Wrapper class for a 128 bit AES encryption key
		public class EncryptionKey
		{
			public string Name; // Optional name for this encryption key
			public string Guid; // Optional guid for this encryption key
			public byte[] Key; // AES key

			public bool IsValid() 
				=> Key != null && 
				0 < Key.Length && 
				Guid != null;
		}

		// Wrapper class for all crypto settings
		public class CryptoSettings
		{
			public EncryptionKey  EncryptionKey = null; // AES encyption key
			public SigningKeyPair SigningKey    = null; // RSA public/private key
			public bool bEnablePakSigning = false; // Enable pak signature checking

			// Encrypt the index of the pak file.
			// Stops the pak file being easily accessible by .pak
			public bool bEnablePakIndexEncryption = false;

			// Encrypt all ini files in the pak. Good for game data obsfucation
			public bool bEnablePakIniEncryption = false;

			// Encrypt the uasset files in the pak file. After cooking, uasset files only contain package metadata / nametables / export and import tables. Provides good string data obsfucation without
			// the downsides of full package encryption, with the security drawbacks of still having some data stored unencrypted 
			public bool bEnablePakUAssetEncryption = false;

			// Encrypt all assets data files (including exp and ubulk) in the pak file. Likely to be slow, and to cause high data entropy (bad for delta patching)
			public bool bEnablePakFullAssetEncryption = false;

			// Some platforms have their own data crypto systems, so allow the config settings to totally disable our own crypto
			public bool bDataCryptoRequired = false;

			// Config setting to enable pak signing
			public bool PakEncryptionRequired = true;

			// Config setting to enable pak encryption
			public bool PakSigningRequired = true;
			
			// A set of named encryption keys that can be used to encrypt different sets of data with a different key that is delivered dynamically (i.e. not embedded within the game executable)
			public EncryptionKey[] SecondaryEncryptionKeys;

			public bool IsAnyEncryptionEnabled()
			{
				return (EncryptionKey != null && EncryptionKey.IsValid()) && 
					   (bEnablePakFullAssetEncryption || bEnablePakUAssetEncryption || bEnablePakIndexEncryption || bEnablePakIniEncryption);
			}

			public bool IsPakSigningEnabled()
			{
				return (SigningKey != null && SigningKey.IsValid()) && 
					    bEnablePakSigning;
			}

			public void Save(FileReference InFile)
			{
				DirectoryReference.CreateDirectory(InFile.Directory);
				FileReference.WriteAllText(InFile, FastJSON.JSON.Instance.ToJSON(this, new FastJSON.JSONParameters {}));
			}
		}

		// Helper class for formatting incoming hex signing key strings
		private static string ProcessSigningKeyInputStrings(string InString)
		{
			if (InString.StartsWith("0x"))
			{
				InString = InString.Substring(2);
			}
			return InString.TrimStart('0');
		}
		
		// Helper function for comparing two AES keys
		private static bool CompareKey(byte[] KeyA, byte[] KeyB)
		{
			if (KeyA.Length != KeyB.Length)
			{
				return false;
			}

			for (int Index = 0; Index < KeyA.Length; ++Index)
			{
				if (KeyA[Index] != KeyB[Index])
				{
					return false;
				}
			}

			return true;
		}

		// Parse crypto settings from INI file
		public static CryptoSettings ParseCryptoSettings(DirectoryReference InProjectDirectory, BuildTargetPlatform InTargetPlatform)
		{
			CryptoSettings Settings = new CryptoSettings();
			
			ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, InProjectDirectory, InTargetPlatform);
			Ini.GetBool(Tag.ConfigSection.PlatformCrypto, Tag.ConfigKey.PlatformRequiresDataCrypto, out Settings.bDataCryptoRequired);
			Ini.GetBool(Tag.ConfigSection.PlatformCrypto, Tag.ConfigKey.PakSigningRequired, out Settings.PakSigningRequired);
			Ini.GetBool(Tag.ConfigSection.PlatformCrypto, Tag.ConfigKey.PakEncryptionRequired, out Settings.PakEncryptionRequired);

			{
				// Start by parsing the legacy encryption.ini settings
				Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Encryption, InProjectDirectory, InTargetPlatform);
				Ini.GetBool(Tag.ConfigSection.CoreEncryption, Tag.ConfigKey.SignPak, out Settings.bEnablePakSigning);

				string[] SigningKeyStrings = new string[3];
				Ini.GetString(Tag.ConfigSection.CoreEncryption, Tag.ConfigKey.RSAPrivateexp, out SigningKeyStrings[0]);
				Ini.GetString(Tag.ConfigSection.CoreEncryption, Tag.ConfigKey.RSAModulus,    out SigningKeyStrings[1]);
				Ini.GetString(Tag.ConfigSection.CoreEncryption, Tag.ConfigKey.RSAPublicexp,  out SigningKeyStrings[2]);

				if (String.IsNullOrEmpty(SigningKeyStrings[0]) 
					|| String.IsNullOrEmpty(SigningKeyStrings[1]) 
					|| String.IsNullOrEmpty(SigningKeyStrings[2]))
				{
					SigningKeyStrings = null;
				}
				else
				{
					Settings.SigningKey = new SigningKeyPair();
					Settings.SigningKey.PrivateKey.Exponent = ParseHexStringToByteArray(ProcessSigningKeyInputStrings(SigningKeyStrings[0]), 64);
					Settings.SigningKey.PrivateKey.Modulus  = ParseHexStringToByteArray(ProcessSigningKeyInputStrings(SigningKeyStrings[1]), 64);
					Settings.SigningKey.PublicKey.Exponent  = ParseHexStringToByteArray(ProcessSigningKeyInputStrings(SigningKeyStrings[2]), 64);
					Settings.SigningKey.PublicKey.Modulus   = Settings.SigningKey.PrivateKey.Modulus;

					if ((64 < Settings.SigningKey.PrivateKey.Exponent.Length) ||
						(64 < Settings.SigningKey.PrivateKey.Modulus.Length)  ||
						(64 < Settings.SigningKey.PublicKey.Exponent.Length)  ||
						(64 < Settings.SigningKey.PublicKey.Modulus.Length))
					{
						throw new Exception(string.Format("[{0}] Signing keys parsed from encryption.ini are too long. They must be a maximum of 64 bytes long!", InProjectDirectory));
					}
				}

				Ini.GetBool(Tag.ConfigSection.CoreEncryption, Tag.ConfigKey.EncryptPak, out Settings.bEnablePakIndexEncryption);
				Settings.bEnablePakFullAssetEncryption = false;
				Settings.bEnablePakUAssetEncryption    = false;
				Settings.bEnablePakIniEncryption       = Settings.bEnablePakIndexEncryption;

				Ini.GetString(Tag.ConfigSection.CoreEncryption, Tag.ConfigKey.AESKey, out string EncryptionKeyString);
				Settings.EncryptionKey = new EncryptionKey();

				if (0 < EncryptionKeyString.Length)
				{
					if (EncryptionKeyString.Length < 32)
					{
						Log.WriteLine(LogEventType.Warning, "AES key parsed from encryption.ini is too short. It must be 32 bytes, so will be padded with 0s, giving sub-optimal security!");
					}
					else if (32 < EncryptionKeyString.Length)
					{
						Log.WriteLine(LogEventType.Warning, "AES key parsed from encryption.ini is too long. It must be 32 bytes, so will be truncated!");
					}

					Settings.EncryptionKey.Key = ParseAnsiStringToByteArray(EncryptionKeyString, 32);
				}
			}

			Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Crypto, InProjectDirectory, InTargetPlatform);
			ConfigHierarchySection CryptoSection = Ini.FindSection(Tag.ConfigSection.ScriptCryptoKeySetting);

			// If we have new format crypto keys, read them in over the top of the legacy settings
			if (CryptoSection != null 
				&& 0 < CryptoSection.KeyNames.Count())
			{
				Ini.GetBool(Tag.ConfigSection.ScriptCryptoKeySetting, nameof(Settings.bEnablePakSigning), out Settings.bEnablePakSigning);
				Ini.GetBool(Tag.ConfigSection.ScriptCryptoKeySetting, nameof(Settings.bEnablePakIniEncryption), out Settings.bEnablePakIniEncryption);
				Ini.GetBool(Tag.ConfigSection.ScriptCryptoKeySetting, nameof(Settings.bEnablePakIndexEncryption), out Settings.bEnablePakIndexEncryption);
				Ini.GetBool(Tag.ConfigSection.ScriptCryptoKeySetting, nameof(Settings.bEnablePakUAssetEncryption), out Settings.bEnablePakUAssetEncryption);
				Ini.GetBool(Tag.ConfigSection.ScriptCryptoKeySetting, nameof(Settings.bEnablePakFullAssetEncryption), out Settings.bEnablePakFullAssetEncryption);

				// Parse encryption key
				Ini.GetString(Tag.ConfigSection.ScriptCryptoKeySetting, Tag.ConfigKey.EncryptionKey, out string EncryptionKeyString);
				if (EncryptionKeyString.HasValue())
				{
					Settings.EncryptionKey = new EncryptionKey
					{
						Key  = System.Convert.FromBase64String(EncryptionKeyString),
						Guid = Guid.Empty.ToString(),
						Name = "Embedded"
					};
				}

				// Parse secondary encryption keys
				List<EncryptionKey> SecondaryEncryptionKeys = new List<EncryptionKey>();

				if (Ini.GetArray(Tag.ConfigSection.ScriptCryptoKeySetting, Tag.ConfigKey.SecondaryEncryptionKeys, out List<string> SecondaryEncryptionKeyStrings))
				{
					foreach (string KeySource in SecondaryEncryptionKeyStrings)
					{
						EncryptionKey NewKey = new EncryptionKey();
						SecondaryEncryptionKeys.Add(NewKey);

						Regex Search = new Regex("\\(Guid=(?\'Guid\'.*),Name=\\\"(?\'Name\'.*)\\\",Key=\\\"(?\'Key\'.*)\\\"\\)");
						Match Match = Search.Match(KeySource);
						if (Match.Success)
						{
							foreach (string GroupName in Search.GetGroupNames())
							{
								string Value = Match.Groups[GroupName].Value;
								if (GroupName == "Guid")
								{
									NewKey.Guid = Value;
								}
								else if (GroupName == "Name")
								{
									NewKey.Name = Value;
								}
								else if (GroupName == "Key")
								{
									NewKey.Key = System.Convert.FromBase64String(Value);
								}
							}
						}
					}
				}

				Settings.SecondaryEncryptionKeys = SecondaryEncryptionKeys.ToArray();

				// Parse signing key
				Ini.GetString(Tag.ConfigSection.ScriptCryptoKeySetting, Tag.ConfigKey.SigningPrivateExponent, out string PrivateExponent);
				Ini.GetString(Tag.ConfigSection.ScriptCryptoKeySetting, Tag.ConfigKey.SigningModulus,         out string Modulus);
				Ini.GetString(Tag.ConfigSection.ScriptCryptoKeySetting, Tag.ConfigKey.SigningPublicExponent,  out string PublicExponent);

				if (PrivateExponent.HasValue() &&
					PublicExponent.HasValue()  &&
					Modulus.HasValue())
				{
					Settings.SigningKey = new SigningKeyPair();
					Settings.SigningKey.PublicKey.Exponent  = System.Convert.FromBase64String(PublicExponent);
					Settings.SigningKey.PublicKey.Modulus   = System.Convert.FromBase64String(Modulus);
					Settings.SigningKey.PrivateKey.Exponent = System.Convert.FromBase64String(PrivateExponent);
					Settings.SigningKey.PrivateKey.Modulus  = Settings.SigningKey.PublicKey.Modulus;
				}
			}

			// Parse project dynamic keychain keys
			if (InProjectDirectory != null)
			{
				ConfigHierarchy GameIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Game, InProjectDirectory, InTargetPlatform);
				if (GameIni != null)
				{
					if (GameIni.GetString(Tag.ConfigSection.ContentEncryption, Tag.ConfigKey.ProjectKeyChain, out string Filename))
					{
						FileReference ProjectKeyChainFile = FileReference.Combine(InProjectDirectory, Tag.Directory.Content, Filename);
						if (FileReference.Exists(ProjectKeyChainFile))
						{
							List<EncryptionKey> EncryptionKeys = new List<EncryptionKey>();

							if (Settings.SecondaryEncryptionKeys != null)
							{
								EncryptionKeys.AddRange(Settings.SecondaryEncryptionKeys);
							}

							string[] Lines = FileReference.ReadAllLines(ProjectKeyChainFile);
							foreach (string Line in Lines)
							{
								string[] KeyParts = Line.Split(':');
								if (KeyParts.Length == 4)
								{
									EncryptionKey NewKey = new EncryptionKey
									{
										Name = KeyParts[0],
										Guid = KeyParts[2],
										Key = System.Convert.FromBase64String(KeyParts[3])
									};

									EncryptionKey ExistingKey = EncryptionKeys.Find((EncryptionKey OtherKey) => { return OtherKey.Guid == NewKey.Guid; });
									if (ExistingKey != null 
										&& !CompareKey(ExistingKey.Key, NewKey.Key))
									{
										throw new Exception("Found multiple encryption keys with the same guid but different AES keys while merging secondary keys from the project key-chain!");
									}

									EncryptionKeys.Add(NewKey);
								}
							}

							Settings.SecondaryEncryptionKeys = EncryptionKeys.ToArray();
						}
					}
				}
			}

			if (!Settings.bDataCryptoRequired)
			{
				CryptoSettings NewSettings = new CryptoSettings { SecondaryEncryptionKeys = Settings.SecondaryEncryptionKeys };
				Settings = NewSettings;
			}
			else
			{
				if (!Settings.PakSigningRequired)
				{
					Settings.bEnablePakSigning = false;
					Settings.SigningKey = null;
				}

				if (!Settings.PakEncryptionRequired)
				{
					Settings.bEnablePakFullAssetEncryption = false;
					Settings.bEnablePakIndexEncryption     = false;
					Settings.bEnablePakIniEncryption       = false;
					Settings.EncryptionKey = null;
					Settings.SigningKey    = null;
				}
			}

			// Check if we have a valid signing key that is of the old short form
			if (Settings.SigningKey != null 
				&& Settings.SigningKey.IsValid() 
				&& Settings.SigningKey.IsUnsecureLegacyKey())
			{
				Log.TraceWarningOnce("Project signing keys found in '{0}' are of the old insecure short format. Please regenerate them using the project crypto settings panel in the editor!", InProjectDirectory);
			}

			// Validate the settings we have read
			if (Settings.bDataCryptoRequired && Settings.bEnablePakSigning && (Settings.SigningKey == null || !Settings.SigningKey.IsValid()))
			{
				Log.TraceWarningOnce("Pak signing is enabled, but no valid signing keys were found. Please generate a key in the editor project crypto settings. Signing will be disabled");
				Settings.bEnablePakSigning = false;
			}

			bool bEncryptionKeyValid = 
				(Settings.EncryptionKey != null && 
				 Settings.EncryptionKey.IsValid());

			bool bAnyEncryptionRequested = 
				Settings.bEnablePakFullAssetEncryption || 
				Settings.bEnablePakIndexEncryption     || 
				Settings.bEnablePakIniEncryption       || 
				Settings.bEnablePakUAssetEncryption;
			
			if (Settings.bDataCryptoRequired && 
				bAnyEncryptionRequested && 
				!bEncryptionKeyValid)
			{
				Log.TraceWarningOnce("Pak encryption is enabled, but no valid encryption key was found. Please generate a key in the editor project crypto settings. Encryption will be disabled");
				Settings.bEnablePakUAssetEncryption    = false;
				Settings.bEnablePakFullAssetEncryption = false;
				Settings.bEnablePakIndexEncryption     = false;
				Settings.bEnablePakIniEncryption       = false;
			}

			return Settings;
		}

		// Take a hex string and parse into an array of bytes
		private static byte[] ParseHexStringToByteArray(string InString, int InMinimumLength)
		{
			if (InString.StartsWith("0x"))
			{
				InString = InString.Substring(2);
			}

			List<byte> Bytes = new List<byte>();
			while (0 < InString.Length)
			{
				int CharsToParse = Math.Min(2, InString.Length);
				string Value = InString.Substring(InString.Length - CharsToParse);
				InString = InString.Substring(0, InString.Length - CharsToParse);
				Bytes.Add(byte.Parse(Value, System.Globalization.NumberStyles.AllowHexSpecifier));
			}

			while (Bytes.Count < InMinimumLength)
			{
				Bytes.Add(0);
			}

			return Bytes.ToArray();
		}

		private static byte[] ParseAnsiStringToByteArray(string InString, Int32 InRequiredLength)
		{
			List<byte> Bytes = new List<byte>();

			if (InRequiredLength < InString.Length)
			{
				InString = InString.Substring(0, InRequiredLength);
			}

			foreach (char C in InString)
			{
				Bytes.Add((byte)C);
			}

			while (Bytes.Count < InRequiredLength)
			{
				Bytes.Add(0);
			}

			return Bytes.ToArray();
		}
	}
}
