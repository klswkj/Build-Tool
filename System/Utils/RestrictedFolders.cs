using System;
using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	// Names of restricted folders. Note: The name of each entry is used to search for/create folders
	public partial struct RestrictedFolder
	{
		private readonly int Id; // Unique Id for this folder

		// Set of permitted references for each restricted folder. Determined via data-driven platform info.
		private static Dictionary<RestrictedFolder, RestrictedFolder[]> PermittedReferences;

		private static readonly UniqueStringRegistry StringRegistry = new UniqueStringRegistry(); // Mapping for unique ids
		private static string[]           Names;
		private static RestrictedFolder[] Values;

		private RestrictedFolder(int Id) 
			=> this.Id = Id;

		// Creates a restricted folder instance from a string
		static private RestrictedFolder FindOrAddByName(string Name) 
			=> new RestrictedFolder(StringRegistry.FindOrAddByName(Name));

		public static bool operator ==(RestrictedFolder A, RestrictedFolder B) => A.Id == B.Id;
		public static bool operator !=(RestrictedFolder A, RestrictedFolder B) => A.Id != B.Id;

		// Tests whether two restricted folder instances are equal
		public override bool Equals(object Other) 
			=> Other is RestrictedFolder folder 
			&& folder.Id == Id;

		// Gets a hash code for this object
		public override int GetHashCode() => Id;

		// Returns an array of folders which are allowed to be referenced from this restricted folder
		public IEnumerable<RestrictedFolder> GetPermittedReferences()
		{
			AddConfidentialPlatforms();

			if (PermittedReferences.TryGetValue(this, out RestrictedFolder[] References))
			{
				foreach (RestrictedFolder Reference in References)
				{
					yield return Reference;
				}
			}
		}

		// Creates entries for all the confidential platforms.
		// Should be called before returning any list of all folder values.
		private static void AddConfidentialPlatforms()
		{
			if (PermittedReferences == null)
			{
				Dictionary<RestrictedFolder, RestrictedFolder[]> NewPermittedReferences = new Dictionary<RestrictedFolder, RestrictedFolder[]>();
				foreach (KeyValuePair<string, DataDrivenPlatformInfo.ConfigDataDrivenPlatformInfo> Pair in DataDrivenPlatformInfo.GetAllPlatformInfos())
				{
					if (Pair.Value.bIsConfidential)
					{
						RestrictedFolder Folder = FindOrAddByName(Pair.Key);
						if (Pair.Value.AdditionalRestrictedFolders != null && 
							0 < Pair.Value.AdditionalRestrictedFolders.Length)
						{
							RestrictedFolder[] References  = Array.ConvertAll(Pair.Value.AdditionalRestrictedFolders, x => FindOrAddByName(x));
							NewPermittedReferences[Folder] = References;
						}
					}
				}

				PermittedReferences = NewPermittedReferences;
			}
		}

		// Gets an array of all the restricted folder names
		public static string[] GetNames()
		{
			if(Names == null)
			{
				AddConfidentialPlatforms();
				Names = StringRegistry.GetStringNames();
			}
			return Names;
		}

		// Ensures that we've added all the restricted folders, and return an array of them
		public static RestrictedFolder[] GetValues()
		{
			if(Values == null)
			{
				AddConfidentialPlatforms();
				Values = Array.ConvertAll(StringRegistry.GetStringIds(), x => new RestrictedFolder(x));
			}

			return Values;
		}

		// Return the string representation
		public override string ToString() => StringRegistry.GetStringForId(Id);
	}

	// Utility functions for getting restricted folder
	public static class RestrictedFolders
	{
		// Finds all the restricted folder names relative to a base directory
		public static List<RestrictedFolder> FindRestrictedFolders(DirectoryReference BaseDir, DirectoryReference OtherDirToCheck)
		{
			List<RestrictedFolder> OutFolders = new List<RestrictedFolder>();

			if (OtherDirToCheck.IsUnderDirectory(BaseDir))
			{
				foreach (RestrictedFolder Value in RestrictedFolder.GetValues())
				{
					string Name = Value.ToString();
					if (OtherDirToCheck.ContainsName(Name, BaseDir.FullName.Length))
					{
						OutFolders.Add(Value);
					}
				}
			}

			return OutFolders;
		}

		// Finds all the permitted restricted folder references for a given path=
		public static List<RestrictedFolder> FindPermittedRestrictedFolderReferences(DirectoryReference BaseDirToStandard, DirectoryReference OtherDirToCheck)
		{
			List<RestrictedFolder> Folders = FindRestrictedFolders(BaseDirToStandard, OtherDirToCheck);

			for (int Idx = 0; Idx < Folders.Count; ++Idx)
			{
				foreach (RestrictedFolder Folder in Folders[Idx].GetPermittedReferences())
				{
					if (!Folders.Contains(Folder))
					{
						Folders.Add(Folder);
					}
				}
			}

			return Folders;
		}
	}
}
