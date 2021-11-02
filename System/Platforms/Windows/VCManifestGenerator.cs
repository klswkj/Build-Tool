using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using BuildToolUtilities;
using System.Xml.Linq;

namespace BuildTool
{
    abstract class VCManifestGenerator
    {
        protected virtual string Schema2010NS => "http://schemas.microsoft.com/appx/2010/manifest";
        protected virtual string Schema2013NS => "http://schemas.microsoft.com/appx/2013/manifest";

        protected virtual string IniSection_PlatformTargetSettings => string.Format("/Script/{0}PlatformEditor.{0}TargetSettings", Platform.ToString());
        protected virtual string IniSection_GeneralProjectSettings => "/Script/EngineSettings.GeneralProjectSettings";
		protected virtual string BuildResourceProjectRelativePath  => "Build\\" + Platform.ToString();

		protected const string BuildResourceSubPath  = "Resources";
		protected const string EngineResourceSubPath = "DefaultImages";

        protected virtual BuildTargetPlatform ConfigPlatform => Platform;

        // Manifest compliance values
        protected const int MaxResourceEntries = 200;

        // INI configuration cache
        protected ConfigHierarchy EngineIni;
        protected ConfigHierarchy GameIni;

        protected string       DefaultCulture;
        protected List<string> CulturesToStage;

        protected ResXWriter                     DefaultResourceWriter;
        protected Dictionary<string, ResXWriter> PerCultureResourceWriters;
        protected BuildTargetPlatform           Platform;
		protected FileReference                  ProjectFile;
        protected string                         ProjectPath;
        protected string                         OutputPath;
        protected string                         IntermediatePath;

		protected List<string> UpdatedFilePaths;

        // Create a manifest generator for the given platform variant.
        public VCManifestGenerator(BuildTargetPlatform InPlatform) => this.Platform = InPlatform;

        protected static bool SafeGetBool(IDictionary<string, string> InDictionary, string Key, bool DefaultValue = false)
		{
			if (InDictionary.ContainsKey(Key))
			{
				string Value = InDictionary[Key].Trim().ToLower();
				return Value == "true" || Value == "1" || Value == "yes";
			}

			return DefaultValue;
		}

        protected static bool CreateCheckDirectory(string TargetDirectory)
		{
			if (!Directory.Exists(TargetDirectory))
			{
				try
				{
					Directory.CreateDirectory(TargetDirectory);
				}
				catch (Exception)
				{
					Log.TraceError("Could not create directory {0}.", TargetDirectory);
					return false;
				}
				if (!Directory.Exists(TargetDirectory))
				{
					Log.TraceError("Path {0} does not exist or is not a directory.", TargetDirectory);
					return false;
				}
			}
			return true;
		}

        protected static void RecursivelyForceDeleteDirectory(string InDirectoryToDelete)
		{
			if (Directory.Exists(InDirectoryToDelete))
			{
				try
				{
					List<string> SubDirectories = new List<string>(Directory.GetDirectories(InDirectoryToDelete, "*.*", SearchOption.AllDirectories));
					foreach (string DirectoryToRemove in SubDirectories)
					{
						RecursivelyForceDeleteDirectory(DirectoryToRemove);
					}
					List<string> FilesInDirectory = new List<string>(Directory.GetFiles(InDirectoryToDelete));
					foreach (string FileToRemove in FilesInDirectory)
					{
						try
						{
							FileAttributes Attributes = File.GetAttributes(FileToRemove);
							if ((Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
							{
								Attributes &= ~FileAttributes.ReadOnly;
								File.SetAttributes(FileToRemove, Attributes);
							}
							File.Delete(FileToRemove);
						}
						catch (Exception)
						{
							Log.TraceWarning("Could not remove file {0} to remove directory {1}.", FileToRemove, InDirectoryToDelete);
						}
					}
					Directory.Delete(InDirectoryToDelete, true);
				}
				catch (Exception)
				{
					Log.TraceWarning("Could not remove directory {0}.", InDirectoryToDelete);
				}
			}
		}

        // Runs external program. Blocking.
        // <param name="Executable">  Executable</param>
        // <param name="CommandLine">  Commandline</param>
        // <returns>bool    Application ran successfully</returns>
        protected bool RunExternalProgram(string Executable, string CommandLine)
        {
			if (File.Exists(Executable) == false)
			{
				throw new BuildException("BUILD FAILED: Couldn't find the executable to Run: {0}", Executable);
			}

            string StdOutString = StringUtils.RunLocalProcessAndReturnStdOut(Executable, CommandLine, out int ExitCode, (Log.OutputLevel >= LogEventType.Verbose));

            if (ExitCode == 0)
			{
				return true;
			}
			else
			{
				Log.TraceError(Path.GetFileName(Executable) + " returned an error.\nApplication output:\n" + StdOutString);
				return false;
			}
        }

        protected string ValidatePackageVersion(string InVersionNumber)
		{
			string WorkingVersionNumber = Regex.Replace(InVersionNumber, "[^.0-9]", "");
			string CompletedVersionString = "";
			if (WorkingVersionNumber != null)
			{
				string[] SplitVersionString = WorkingVersionNumber.Split(new char[] { '.' });
				int NumVersionElements = Math.Min(4, SplitVersionString.Length);
				for (int VersionElement = 0; VersionElement < NumVersionElements; VersionElement++)
				{
					string QuadElement = SplitVersionString[VersionElement];

                    if (QuadElement.Length == 0 || !int.TryParse(QuadElement, out int QuadValue))
                    {
                        CompletedVersionString += "0";
                    }
                    else
                    {
                        if (QuadValue < 0)
                        {
                            QuadValue = 0;
                        }
                        if (65535 < QuadValue)
                        {
                            QuadValue = 65535;
                        }
                        CompletedVersionString += QuadValue;
                    }
                    if (VersionElement < 3)
					{
						CompletedVersionString += ".";
					}
				}
				for (int VersionElement = NumVersionElements; VersionElement < 4; ++VersionElement)
				{
					CompletedVersionString += "0";

					if (VersionElement < 3)
					{
						CompletedVersionString += ".";
					}
				}
			}
			if (CompletedVersionString == null || CompletedVersionString.Length <= 0)
			{
				Log.TraceError("Invalid package version {0}. Package versions must be in the format #.#.#.# where # is a number 0-65535.", InVersionNumber);
				Log.TraceError("Consider setting [{0}]:PackageVersion to provide a specific value.", IniSection_PlatformTargetSettings);
			}
			return CompletedVersionString;
		}

        protected string ValidateProjectBaseName(string InApplicationId)
		{
			string ReturnVal = Regex.Replace(InApplicationId, "[^A-Za-z0-9]", "");
			if (ReturnVal != null)
			{
				// Remove any leading numbers (must start with a letter)
				ReturnVal = Regex.Replace(ReturnVal, "^[0-9]*", "");
			}
			if (ReturnVal == null || ReturnVal.Length <= 0)
			{
				Log.TraceError("Invalid application ID {0}. Application IDs must only contain letters and numbers. And they must begin with a letter.", InApplicationId);
				Log.TraceError("Consider using the setting [{0}]:PackageName to provide a specific value.", IniSection_PlatformTargetSettings);
			}
			return ReturnVal;
		}

        protected string ReadIniString(string Key, string Section, string DefaultValue = null)
		{
			if (Key == null)
            {
                return DefaultValue;
            }

            if (GameIni.GetString(Section, Key, out string Value) && !string.IsNullOrWhiteSpace(Value))
            {
                return Value;
            }

            return EngineIni.GetString(Section, Key, out Value) && !string.IsNullOrWhiteSpace(Value) ? Value : DefaultValue;
        }

        protected string GetConfigString(string PlatformKey, string GenericKey, string DefaultValue = null)
		{
			string GenericValue = ReadIniString(GenericKey, IniSection_GeneralProjectSettings, DefaultValue);
			return ReadIniString(PlatformKey, IniSection_PlatformTargetSettings, GenericValue);
		}

        protected bool GetConfigBool(string PlatformKey, string GenericKey, bool DefaultValue = false)
		{
			string GenericValue = ReadIniString(GenericKey, IniSection_GeneralProjectSettings, null);
			string ResultStr    = ReadIniString(PlatformKey, IniSection_PlatformTargetSettings, GenericValue);

			if (ResultStr == null)
            {
                return DefaultValue;
            }

            ResultStr = ResultStr.Trim().ToLower();

			return ResultStr == "true" || ResultStr == "1" || ResultStr == "yes";
		}

        protected string GetConfigColor(string PlatformConfigKey, string DefaultValue)
		{
			string ConfigValue = GetConfigString(PlatformConfigKey, null, null);
			if (ConfigValue == null)
				{
                return DefaultValue;
            }

            if (ConfigHierarchy.TryParse(ConfigValue, out Dictionary<string, string> Pairs) &&
                int.TryParse(Pairs["R"], out int R) &&
                int.TryParse(Pairs["G"], out int G) &&
                int.TryParse(Pairs["B"], out int B))
            {
                return "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");
            }

            Log.TraceWarning("Failed to parse color config value. Using default.");
			return DefaultValue;
		}

        protected bool CopyAndReplaceBinaryIntermediate(string ResourceFileName, bool AllowEngineFallback = true)
		{
			string TargetPath = Path.Combine(IntermediatePath, BuildResourceSubPath);
			string SourcePath = Path.Combine(ProjectPath, BuildResourceProjectRelativePath, BuildResourceSubPath);

			// Try falling back to the engine defaults if requested
			bool bFileExists = File.Exists(Path.Combine(SourcePath, ResourceFileName));
			if (!bFileExists)
			{
				if (AllowEngineFallback)
				{
					SourcePath = Path.Combine(BuildTool.EngineDirectory.FullName, BuildResourceProjectRelativePath, EngineResourceSubPath);
					bFileExists = File.Exists(Path.Combine(SourcePath, ResourceFileName));

					// look in Platform extensions too
					if (!bFileExists)
					{
						SourcePath = Path.Combine(BuildTool.EnginePlatformExtensionsDirectory.FullName, Platform.ToString(), "Build", EngineResourceSubPath);
						bFileExists = File.Exists(Path.Combine(SourcePath, ResourceFileName));
					}
				}
			}

			// At least the default culture entry for any resource binary must always exist
			if (!bFileExists)
			{
				return false;
			}

			// If the target resource folder doesn't exist yet, create it
			if (!CreateCheckDirectory(TargetPath))
			{
				return false;
			}

			// Find all copies of the resource file in the source directory (could be up to one for each culture and the default).
			IEnumerable<string> SourceResourceInstances = Directory.EnumerateFiles(SourcePath, ResourceFileName, SearchOption.AllDirectories);

			// Copy new resource files
			foreach (string SourceResourceFile in SourceResourceInstances)
			{
				//@todo only copy files for cultures we are staging
				string TargetResourcePath = Path.Combine(TargetPath, SourceResourceFile.Substring(SourcePath.Length + 1));
				if (!CreateCheckDirectory(Path.GetDirectoryName(TargetResourcePath)))
				{
					Log.TraceError("Unable to create intermediate directory {0}.", Path.GetDirectoryName(TargetResourcePath));
					continue;
				}
				if (!File.Exists(TargetResourcePath))
				{
					try
					{
						File.Copy(SourceResourceFile, TargetResourcePath);

						// File.Copy also copies the attributes, so make sure the new file isn't read only
						FileAttributes Attrs = File.GetAttributes(TargetResourcePath);
						if (Attrs.HasFlag(FileAttributes.ReadOnly))
						{
							File.SetAttributes(TargetResourcePath, Attrs & ~FileAttributes.ReadOnly);
						}
					}
					catch (Exception)
					{
						Log.TraceError("Unable to copy file {0} to {1}.", SourceResourceFile, TargetResourcePath);
						return false;
					}
				}
			}

			return true;
		}

        protected void CompareAndReplaceModifiedTarget(string IntermediatePath, string TargetPath)
		{
			if (!File.Exists(IntermediatePath))
			{
				Log.TraceError("Tried to copy non-existant intermediate file {0}.", IntermediatePath);
				return;
			}

			CreateCheckDirectory(Path.GetDirectoryName(TargetPath));

			// Check for differences in file contents
			if (File.Exists(TargetPath))
			{
				byte[] OriginalContents = File.ReadAllBytes(TargetPath);
				byte[] NewContents = File.ReadAllBytes(IntermediatePath);
				if (!OriginalContents.Equals(NewContents))
				{
					try
					{
						FileAttributes Attrs = File.GetAttributes(TargetPath);
						if ((Attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
						{
							Attrs &= ~FileAttributes.ReadOnly;
							File.SetAttributes(TargetPath, Attrs);
						}
						File.Delete(TargetPath);
					}
					catch (Exception)
					{
						Log.TraceError("Could not replace file {0}.", TargetPath);
						return;
					}
				}
			}

			// If the file is present it is unmodified and should not be overwritten
			if (!File.Exists(TargetPath))
			{
				try
				{
					File.Copy(IntermediatePath, TargetPath);
				}
				catch (Exception)
				{
					Log.TraceError("Unable to copy file {0}.", TargetPath);
					return;
				}
				UpdatedFilePaths.Add(TargetPath);
			}
		}

        protected void CopyResourcesToTargetDir()
		{
			string TargetPath = Path.Combine(OutputPath, BuildResourceSubPath);
			string SourcePath = Path.Combine(IntermediatePath, BuildResourceSubPath);

			// If the target resource folder doesn't exist yet, create it
			if (!CreateCheckDirectory(TargetPath))
			{
				return;
			}

			// Find all copies of the resource file in both target and source directories (could be up to one for each culture and the default, but must have at least the default).
			var TargetResourceInstances = Directory.EnumerateFiles(TargetPath, "*.*", SearchOption.AllDirectories);
			var SourceResourceInstances = Directory.EnumerateFiles(SourcePath, "*.*", SearchOption.AllDirectories);

			// Remove any target files that aren't part of the source file list
			foreach (string TargetResourceFile in TargetResourceInstances)
			{
				// Ignore string tables (the only non-binary resources that will be present)
				if (!TargetResourceFile.Contains(".resw"))
				{
					//@todo always delete for cultures we aren't staging
					bool bRelativeSourceFileFound = false;
					foreach (string SourceResourceFile in SourceResourceInstances)
					{
						string SourceRelativeFile = SourceResourceFile.Substring(SourcePath.Length + 1);
						string TargetRelativeFile = TargetResourceFile.Substring(TargetPath.Length + 1);
						if (SourceRelativeFile.Equals(TargetRelativeFile))
						{
							bRelativeSourceFileFound = true;
							break;
						}
					}
					if (!bRelativeSourceFileFound)
					{
						try
						{
							File.Delete(TargetResourceFile);
						}
						catch (Exception E)
						{
							Log.TraceError("Could not remove stale resource file {0} - {1}.", TargetResourceFile, E.Message);
						}
					}
				}
			}

			// Copy new resource files only if they differ from the destination
			foreach (string SourceResourceFile in SourceResourceInstances)
			{
				//@todo only copy files for cultures we are staging
				string TargetResourcePath = Path.Combine(TargetPath, SourceResourceFile.Substring(SourcePath.Length + 1));
				CompareAndReplaceModifiedTarget(SourceResourceFile, TargetResourcePath);
			}
		}

        protected void AddResourceEntry(string ResourceEntryName, string ConfigKey, string GenericINISection, string GenericINIKey, string DefaultValue, string ValueSuffix = "")
		{
			string ConfigScratchValue = null;

            // Get the default culture value
            if (EngineIni.GetString(IniSection_PlatformTargetSettings, "CultureStringResources", out string DefaultCultureScratchValue))
            {
                if (!ConfigHierarchy.TryParse(DefaultCultureScratchValue, out Dictionary<string, string> Values))
                {
                    Log.TraceError("Invalid default culture string resources: \"{0}\". Unable to add resource entry.", DefaultCultureScratchValue);
                    return;
                }

                ConfigScratchValue = Values[ConfigKey];
            }

            if (string.IsNullOrEmpty(ConfigScratchValue))
			{
				// No platform specific value is provided. Use the generic config or default value
				ConfigScratchValue = ReadIniString(GenericINIKey, GenericINISection, DefaultValue);
			}

			DefaultResourceWriter.AddResource(ResourceEntryName, ConfigScratchValue + ValueSuffix);

            // Find the default value
            if (EngineIni.GetArray(IniSection_PlatformTargetSettings, "PerCultureResources", out List<string> PerCultureValues))
            {
                foreach (string CultureCombinedValues in PerCultureValues)
                {
                    if (!ConfigHierarchy.TryParse(CultureCombinedValues, out Dictionary<string, string> SeparatedCultureValues) ||
                        !SeparatedCultureValues.ContainsKey("CultureStringResources") ||
                        !SeparatedCultureValues.ContainsKey("CultureId"))
                    {
                        Log.TraceError("Invalid per-culture resource: \"{0}\". Unable to add resource entry.", CultureCombinedValues);
                        continue;
                    }

                    string CultureId = SeparatedCultureValues["CultureId"];

                    if (CulturesToStage.Contains(CultureId))
                    {
                        if (!ConfigHierarchy.TryParse(SeparatedCultureValues["CultureStringResources"], out Dictionary<string, string> CultureStringResources))
                        {
                            Log.TraceError("Invalid culture string resources: \"{0}\". Unable to add resource entry.", CultureCombinedValues);
                            continue;
                        }

                        string ConfigValue = CultureStringResources[ConfigKey];

                        if (CulturesToStage.Contains(CultureId) && !string.IsNullOrEmpty(ConfigValue))
                        {
                            ResXWriter ResourceWriter = PerCultureResourceWriters[CultureId];
                            ResourceWriter.AddResource(ResourceEntryName, ConfigValue + ValueSuffix);
                        }
                    }
                }
            }
        }

        protected virtual XName GetName( string BaseName, string SchemaName )
		{
			return XName.Get(BaseName);
		}

        protected XElement GetResources()
		{
			var ResourceCulturesList = CulturesToStage.ToList();
			// Move the default culture to the front of the list
			ResourceCulturesList.Remove(DefaultCulture);
			ResourceCulturesList.Insert(0, DefaultCulture);

			// Check that we have a valid number of cultures
			if (CulturesToStage.Count < 1 || MaxResourceEntries <= CulturesToStage.Count)
			{
				Log.TraceWarning("Incorrect number of cultures to stage. There must be between 1 and {0} cultures selected.", MaxResourceEntries);
			}

            // Create the culture list. This list is unordered except that the default language must be first which we already took care of above.
            IEnumerable<XElement> CultureElements = ResourceCulturesList.Select(c => new XElement(GetName("Resource", Schema2010NS), new XAttribute("Language", c)));

			return new XElement(GetName("Resources", Schema2010NS), CultureElements);
		}

        protected abstract string GetSDKDirectory();

		protected abstract string GetMakePriBinaryPath();

		protected abstract XElement GetManifest(List<TargetConfiguration> TargetConfigs, List<string> Executables, out string IdentityName);

		protected virtual void ProcessManifest(List<TargetConfiguration> TargetConfigs, List<string> Executables, string ManifestName, string ManifestTargetPath, string ManifestIntermediatePath)
        {
		}

        public List<string> CreateManifest(string InManifestName, string InOutputPath, string InIntermediatePath, FileReference InProjectFile, string InProjectDirectory, List<TargetConfiguration> InTargetConfigs, List<string> InExecutables)
		{
			// Verify we can find the SDK.
			string SDKDirectory = GetSDKDirectory();
			if (string.IsNullOrEmpty(SDKDirectory))
			{
				return null;
			}

			// Check parameter values are valid.
			if (InTargetConfigs.Count != InExecutables.Count)
			{
				Log.TraceError("The number of target configurations ({0}) and executables ({1}) passed to manifest generation do not match.", InTargetConfigs.Count, InExecutables.Count);
				return null;
			}
			if (InTargetConfigs.Count < 1)
			{
				Log.TraceError("The number of target configurations is zero, so we cannot generate a manifest.");
				return null;
			}

			if (!CreateCheckDirectory(InOutputPath))
			{
				Log.TraceError("Failed to create output directory \"{0}\".", InOutputPath);
				return null;
			}
			if (!CreateCheckDirectory(InIntermediatePath))
			{
				Log.TraceError("Failed to create intermediate directory \"{0}\".", InIntermediatePath);
				return null;
			}

			OutputPath = InOutputPath;
			IntermediatePath = InIntermediatePath;
			ProjectFile = InProjectFile;
			ProjectPath = InProjectDirectory;
			UpdatedFilePaths = new List<string>();

			// Load up INI settings. We'll use engine settings to retrieve the manifest configuration, but these may reference
			// values in either game or engine settings, so we'll keep both.
			GameIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Game, DirectoryReference.FromFile(InProjectFile), ConfigPlatform);
			EngineIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, DirectoryReference.FromFile(InProjectFile), ConfigPlatform);

			// Load and verify/clean culture list
			{
                GameIni.GetArray("/Script/EditorEd.ProjectPackagingSettings", "CulturesToStage", out List<string> CulturesToStageWithDuplicates);
                GameIni.GetString("/Script/EditorEd.ProjectPackagingSettings", "DefaultCulture", out DefaultCulture);

				if (CulturesToStageWithDuplicates == null || CulturesToStageWithDuplicates.Count < 1)
				{
					Log.TraceError("At least one culture must be selected to stage.");
					return null;
				}

				CulturesToStage = CulturesToStageWithDuplicates.Distinct().ToList();
			}
			if (DefaultCulture == null || DefaultCulture.Length < 1)
			{
				DefaultCulture = CulturesToStage[0];
				Log.TraceWarning("A default culture must be selected to stage. Using {0}.", DefaultCulture);
			}
			if (!CulturesToStage.Contains(DefaultCulture))
			{
				DefaultCulture = CulturesToStage[0];
				Log.TraceWarning("The default culture must be one of the staged cultures. Using {0}.", DefaultCulture);
			}

            if (EngineIni.GetArray(IniSection_PlatformTargetSettings, "PerCultureResources", out List<string> PerCultureValues))
            {
                foreach (string CultureCombinedValues in PerCultureValues)
                {
                    if (!ConfigHierarchy.TryParse(CultureCombinedValues, out Dictionary<string, string> SeparatedCultureValues))
                    {
                        Log.TraceWarning("Invalid per-culture resource value: {0}", CultureCombinedValues);
                        continue;
                    }

                    string StageId = SeparatedCultureValues["StageId"];
                    int CultureIndex = CulturesToStage.FindIndex(x => x == StageId);
                    if (0 <= CultureIndex)
                    {
                        CulturesToStage[CultureIndex] = SeparatedCultureValues["CultureId"];
                        if (DefaultCulture == StageId)
                        {
                            DefaultCulture = SeparatedCultureValues["CultureId"];
                        }
                    }
                }
            }

            // Only warn if shipping, we can run without translated cultures they're just needed for cert
            else if (InTargetConfigs.Contains(TargetConfiguration.Shipping))
            {
                Log.TraceInformation("Staged culture mappings not setup in the editor. See Per Culture Resources in the {0} Target Settings.", Platform.ToString());
            }

            // Clean out the resources intermediate path so that we know there are no stale binary files.
            string IntermediateResourceDirectory = Path.Combine(IntermediatePath, BuildResourceSubPath);
			RecursivelyForceDeleteDirectory(IntermediateResourceDirectory);
			if (!CreateCheckDirectory(IntermediateResourceDirectory))
			{
				Log.TraceError("Could not create directory {0}.", IntermediateResourceDirectory);
				return null;
			}

			// Construct a single resource writer for the default (no-culture) values
			string DefaultResourceIntermediatePath = Path.Combine(IntermediateResourceDirectory, "resources.resw");
			DefaultResourceWriter = new ResXWriter(DefaultResourceIntermediatePath);

			// Construct the ResXWriters for each culture
			PerCultureResourceWriters = new Dictionary<string, ResXWriter>();
			foreach (string Culture in CulturesToStage)
			{
				string IntermediateStringResourcePath = Path.Combine(IntermediateResourceDirectory, Culture);
				string IntermediateStringResourceFile = Path.Combine(IntermediateStringResourcePath, "resources.resw");

				if (!CreateCheckDirectory(IntermediateStringResourcePath))
				{
					Log.TraceWarning("Culture {0} resources not staged.", Culture);
					CulturesToStage.Remove(Culture);
					if (Culture == DefaultCulture)
					{
						DefaultCulture = CulturesToStage[0];
						Log.TraceWarning("Default culture skipped. Using {0} as default culture.", DefaultCulture);
					}
					continue;
				}

				PerCultureResourceWriters.Add(Culture, new ResXWriter(IntermediateStringResourceFile));
			}

            // Create the manifest document
            XDocument ManifestXmlDocument = new XDocument(GetManifest(InTargetConfigs, InExecutables, out string IdentityName));

            // Export manifest to the intermediate directory then compare the contents to any existing target manifest
            // and replace if there are differences.
            string ManifestIntermediatePath = Path.Combine(IntermediatePath, InManifestName);
			string ManifestTargetPath       = Path.Combine(OutputPath, InManifestName);
			ManifestXmlDocument.Save(ManifestIntermediatePath);
			CompareAndReplaceModifiedTarget(ManifestIntermediatePath, ManifestTargetPath);
			ProcessManifest(InTargetConfigs, InExecutables, InManifestName, ManifestTargetPath, ManifestIntermediatePath);

			// Clean out any resource directories that we aren't staging
			string TargetResourcePath = Path.Combine(OutputPath, BuildResourceSubPath);
			if (Directory.Exists(TargetResourcePath))
			{
				List<string> TargetResourceDirectories = new List<string>(Directory.GetDirectories(TargetResourcePath, "*.*", SearchOption.AllDirectories));
				foreach (string ResourceDirectory in TargetResourceDirectories)
				{
					if (!CulturesToStage.Contains(Path.GetFileName(ResourceDirectory)))
					{
						RecursivelyForceDeleteDirectory(ResourceDirectory);
					}
				}
			}

			// Export the resource tables starting with the default culture
			string DefaultResourceTargetPath = Path.Combine(OutputPath, BuildResourceSubPath, "resources.resw");
			DefaultResourceWriter.Close();
			CompareAndReplaceModifiedTarget(DefaultResourceIntermediatePath, DefaultResourceTargetPath);

			foreach (KeyValuePair<string, ResXWriter> StringResWriterPair in PerCultureResourceWriters)
			{
				StringResWriterPair.Value.Close();

				string IntermediateStringResourceFile = Path.Combine(IntermediateResourceDirectory, StringResWriterPair.Key, "resources.resw");
				string TargetStringResourceFile       = Path.Combine(OutputPath, BuildResourceSubPath, StringResWriterPair.Key, "resources.resw");

				CompareAndReplaceModifiedTarget(IntermediateStringResourceFile, TargetStringResourceFile);
			}

			// Copy all the binary resources into the target directory.
			CopyResourcesToTargetDir();

			// The resource database is dependent on everything else calculated here (manifest, resource string tables, binary resources).
			// So if any file has been updated we'll need to run the config.
			if (0 < UpdatedFilePaths.Count)
			{
				// Create resource index configuration
				string PriExecutable = GetMakePriBinaryPath();
				string ResourceConfigFile = Path.Combine(IntermediatePath, "priconfig.xml");
#pragma warning disable IDE0018 // Inline variable declaration
                bool bEnableAutoResourcePacks = false;
#pragma warning restore IDE0018 // Inline variable declaration
                EngineIni.GetBool(IniSection_PlatformTargetSettings, "bEnableAutoResourcePacks", out bEnableAutoResourcePacks);

				// If the game is not going to support language resource packs then merge the culture qualifiers.
				if (bEnableAutoResourcePacks || CulturesToStage.Count <= 1)
				{
					RunExternalProgram(PriExecutable, "createconfig /cf \"" + ResourceConfigFile + "\" /dq " + DefaultCulture + " /o");
				}
				else
				{
					RunExternalProgram(PriExecutable, "createconfig /cf \"" + ResourceConfigFile + "\" /dq " + String.Join("_", CulturesToStage) + " /o");
				}

				// Modify configuration to restrict indexing to the Resources directory (saves time and space)
				XmlDocument PriConfig = new XmlDocument();
				PriConfig.Load(ResourceConfigFile);

				// If the game is not going to support resource packs then remove the autoResourcePackages.
				if (!bEnableAutoResourcePacks)
				{
					XmlNode PackagingNode = PriConfig.SelectSingleNode("/resources/packaging");
					PackagingNode.ParentNode.RemoveChild(PackagingNode);
				}

				// The previous implementation using startIndexAt="Resources" did not produce the expected ResourceMapSubtree hierarchy, so this manually specifies all resources in a .resfiles instead.
				string ResourcesResFile = Path.Combine(IntermediatePath, "resources.resfiles");

				XmlNode      PriIndexNode  = PriConfig.SelectSingleNode("/resources/index");
				XmlAttribute PriStartIndex = PriIndexNode.Attributes["startIndexAt"];
				PriStartIndex.Value = ResourcesResFile;

				// Swap the default folder indexer-config to a RESFILES indexer-config.
				XmlElement FolderIndexerConfigNode = PriConfig.SelectSingleNode("/resources/index/indexer-config[@type='folder']") as XmlElement;
				FolderIndexerConfigNode.SetAttribute("type", "RESFILES");
				FolderIndexerConfigNode.RemoveAttribute("foldernameAsQualifier");
				FolderIndexerConfigNode.RemoveAttribute("filenameAsQualifier");

				PriConfig.Save(ResourceConfigFile);

				IEnumerable<string> Resources = Directory.EnumerateFiles(Path.Combine(OutputPath, BuildResourceSubPath), "*.*", SearchOption.AllDirectories);
				System.Text.StringBuilder ResourcesList = new System.Text.StringBuilder();
				foreach (string Resource in Resources)
				{
					ResourcesList.AppendLine(Resource.Replace(OutputPath, "").TrimStart('\\'));
				}

				File.WriteAllText(ResourcesResFile, ResourcesList.ToString());

				// Remove previous pri files so we can enumerate which ones are new since the resource generator could produce a file for each staged language.
				IEnumerable<string> OldPriFiles = Directory.EnumerateFiles(IntermediatePath, "*.pri");
				foreach (string OldPri in OldPriFiles)
				{
					try
					{
						File.Delete(OldPri);
					}
					catch (Exception)
					{
						Log.TraceError("Could not delete file {0}.", OldPri);
					}
				}

				// Generate the resource index
				string ResourceLogFile   = Path.Combine(IntermediatePath, "ResIndexLog.xml");
				string ResourceIndexFile = Path.Combine(IntermediatePath, "resources.pri");

				string MakePriCommandLine = "new /pr \"" + OutputPath + "\" /cf \"" + ResourceConfigFile + "\" /mn \"" + ManifestTargetPath + "\" /il \"" + ResourceLogFile + "\" /of \"" + ResourceIndexFile + "\" /o";

				if (IdentityName != null)
				{
					MakePriCommandLine += " /indexName \"" + IdentityName + "\"";
				}

				RunExternalProgram(PriExecutable, MakePriCommandLine);

				// Remove any existing pri target files that were not generated by this latest update
				IEnumerable<string> NewPriFiles    = Directory.EnumerateFiles(IntermediatePath, "*.pri");
				IEnumerable<string> TargetPriFiles = Directory.EnumerateFiles(OutputPath,       "*.pri");

				foreach (string TargetPri in TargetPriFiles)
				{
					if (!NewPriFiles.Contains(TargetPri))
					{
						try
						{
							File.Delete(TargetPri);
						}
						catch (Exception)
						{
							Log.TraceError("Could not remove stale file {0}.", TargetPri);
						}
					}
				}

				// Stage all the modified pri files to the output directory
				foreach (string NewPri in NewPriFiles)
				{
					string NewResourceIndexFile   = Path.Combine(IntermediatePath, Path.GetFileName(NewPri));
					string FinalResourceIndexFile = Path.Combine(OutputPath, Path.GetFileName(NewPri));

					CompareAndReplaceModifiedTarget(NewResourceIndexFile, FinalResourceIndexFile);
				}
			}

			return UpdatedFilePaths;
		}
	}
}
