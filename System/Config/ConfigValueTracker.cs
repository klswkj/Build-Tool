using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Identifier for a config file key, including information about the hierarchy used to read it
	[DebuggerDisplay("{Name}")]
	class ConfigDependencyKey : IEquatable<ConfigDependencyKey>
	{
		public ConfigHierarchyType  HierarchyType;
		public DirectoryReference   ProjectDirToRead; // Project directory to read config files from
		public BuildTargetPlatform PlatformBeingBuilt;
		public string               SectionName;
		public string               KeyName;

		public ConfigDependencyKey(ConfigHierarchyType Type, DirectoryReference ProjectDir, BuildTargetPlatform Platform, string SectionName, string KeyName)
		{
			this.HierarchyType = Type;
			this.ProjectDirToRead = ProjectDir;
			this.PlatformBeingBuilt = Platform;
			this.SectionName = SectionName;
			this.KeyName = KeyName;
		}

		public ConfigDependencyKey(BinaryArchiveReader Reader)
		{
			HierarchyType = (ConfigHierarchyType)Reader.ReadInt();
			ProjectDirToRead = Reader.ReadDirectoryReference();
			PlatformBeingBuilt = Reader.ReadTargetPlatform();
			SectionName = Reader.ReadString();
			KeyName = Reader.ReadString();
		}

		// Writes this key to an archive
		public void Write(BinaryArchiveWriter Writer)
		{
			Writer.WriteInt((int)HierarchyType);
			Writer.WriteDirectoryReference(ProjectDirToRead);
			Writer.WriteTargetPlatform(PlatformBeingBuilt);
			Writer.WriteString(SectionName);
			Writer.WriteString(KeyName);
		}

		// Tests whether this key is equal to another object
		public override bool Equals(object Other)
		{
			return (Other is ConfigDependencyKey key) && Equals(key);
		}

		public bool Equals(ConfigDependencyKey Other)
		{
			return HierarchyType == Other.HierarchyType
				&& ProjectDirToRead == Other.ProjectDirToRead
				&& PlatformBeingBuilt == Other.PlatformBeingBuilt
				&& SectionName == Other.SectionName
				&& KeyName == Other.KeyName;
		}
		
		// Gets a hash code for this object
		// <returns>Hash code for the object</returns>
		public override int GetHashCode()
		{
			int Hash = 17;
			Hash = (Hash * 31) + HierarchyType.GetHashCode();
			Hash = (Hash * 31) + ((ProjectDirToRead == null) ? 0 : ProjectDirToRead.GetHashCode());
			Hash = (Hash * 31) + PlatformBeingBuilt.GetHashCode();
			Hash = (Hash * 31) + SectionName.GetHashCode();
			Hash = (Hash * 31) + KeyName.GetHashCode();
			return Hash;
		}
	}

	// Stores a list of config key/value pairs that have been read
	class ConfigValueTracker
	{
		private readonly Dictionary<ConfigDependencyKey, IReadOnlyList<string>> Dependencies;

		public ConfigValueTracker()
		{
			Dependencies = new Dictionary<ConfigDependencyKey, IReadOnlyList<string>>();
		}

		// Construct an object from an archive on disk
		public ConfigValueTracker(BinaryArchiveReader Reader)
		{
			Dependencies = Reader.ReadDictionary(() => new ConfigDependencyKey(Reader), () => (IReadOnlyList<string>)Reader.ReadList(() => Reader.ReadString()));
		}

		// Write the dependencies object to disk
		public void Write(BinaryArchiveWriter Writer)
		{
			Writer.WriteDictionary(Dependencies, Key => Key.Write(Writer), Value => Writer.WriteList(Value, x => Writer.WriteString(x)));
		}

		// Adds a new configuration value
		public void Add(ConfigHierarchyType InConfigHierarchyType, DirectoryReference ProjectDir, BuildTargetPlatform PlatformBeingBuilt, string SectionName, string KeyName, IReadOnlyList<string> Values)
		{
			ConfigDependencyKey Key = new ConfigDependencyKey(InConfigHierarchyType, ProjectDir, PlatformBeingBuilt, SectionName, KeyName);
			Dependencies[Key] = Values;
		}

		// Checks whether the list of dependencies is still valid
		public bool IsValid()
		{
			foreach(KeyValuePair<ConfigDependencyKey, IReadOnlyList<string>> Pair in Dependencies)
			{
				// Read the appropriate hierarchy
				ConfigHierarchy Hierarchy = ConfigCache.ReadHierarchy(Pair.Key.HierarchyType, Pair.Key.ProjectDirToRead, Pair.Key.PlatformBeingBuilt);

				// Get the value(s) associated with this key
				Hierarchy.TryGetValues(Pair.Key.SectionName, Pair.Key.KeyName, out IReadOnlyList<string> NewValues);

				// Check if they're different
				if (Pair.Value == null)
				{
					if(NewValues != null)
					{
						return false;
					}
				}
				else
				{
					if(NewValues == null || !Enumerable.SequenceEqual(Pair.Value, NewValues, StringComparer.Ordinal))
					{
						return false;
					}
				}
			}
			return true;
		}
	}
}

