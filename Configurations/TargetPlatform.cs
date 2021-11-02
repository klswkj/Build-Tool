using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Serialization;
using BuildToolUtilities;

namespace BuildTool
{
	// The platform we're building for
	[Serializable, TypeConverter(typeof(BuildTargetPlatformTypeConverter))]
	public struct BuildTargetPlatform : ISerializable
	{
		private int Id; // internal concrete name of the group

		// shared string instance registry
		// - pass in a delegate to create a new one with a name that wasn't made yet
		private static UniqueStringRegistry StringRegistry;

		#region Private/boilerplate
		private static UniqueStringRegistry GetUniqueStringRegistry()
		{
			if (StringRegistry == null)
			{
				StringRegistry = new UniqueStringRegistry();
			}
			return StringRegistry;
		}

		private BuildTargetPlatform(string Name)
		{
			Id = GetUniqueStringRegistry().FindOrAddByName(Name);
		}

		private BuildTargetPlatform(int InId)
		{
			Id = InId;
		}

        public void GetObjectData(SerializationInfo Info, StreamingContext Context) 
			=> Info.AddValue("Name", ToString());

        public BuildTargetPlatform(SerializationInfo Info, StreamingContext Context) 
			=> Id = GetUniqueStringRegistry().FindOrAddByName((string)Info.GetValue("Name", typeof(string)));

        // Return the single instance of the Group with this name
        static private BuildTargetPlatform FindOrAddByName(string Name)
		{
			return new BuildTargetPlatform(GetUniqueStringRegistry().FindOrAddByName(Name));
		}

        public static bool operator ==(BuildTargetPlatform A, BuildTargetPlatform B) 
			=> A.Id == B.Id;

        public static bool operator !=(BuildTargetPlatform A, BuildTargetPlatform B) 
			=> A.Id != B.Id;

        public override bool Equals(object B)
        => Id == ((BuildTargetPlatform)B).Id;

        public override int GetHashCode()
		=> Id;

		#endregion

		// Return the string representation
		public override string ToString()
		=> GetUniqueStringRegistry().GetStringForId(Id);

		static public bool TryParse(string Name, out BuildTargetPlatform Platform)
		{
			if (GetUniqueStringRegistry().HasString(Name))
			{
				Platform.Id = GetUniqueStringRegistry().FindOrAddByName(Name);
				return true;
			}

			Platform.Id = -1;
			return false;
		}

		static public BuildTargetPlatform Parse(string Name)
		{
			if (GetUniqueStringRegistry().HasString(Name))
			{
				return new BuildTargetPlatform(Name);
			}

			throw new BuildException(string.Format("The platform name {0} is not a valid platform name. Valid names are ({1})", Name,
				string.Join(",", GetUniqueStringRegistry().GetStringNames())));
		}

        public static BuildTargetPlatform[] GetValidPlatforms() 
			=> Array.ConvertAll(GetUniqueStringRegistry().GetStringIds(), x => new BuildTargetPlatform(x));
        public static string[] GetValidPlatformNames() 
			=> GetUniqueStringRegistry().GetStringNames();

        public static bool IsValidName(string Name) 
			=> GetUniqueStringRegistry().HasString(Name);

        public bool IsInGroup(BuildPlatformGroup Group) 
			=> BuildPlatform.IsPlatformInGroup(this, Group);

        public static BuildTargetPlatform Win32        = FindOrAddByName(Tag.Platform.Win32); // 32-bit Windows
		public static BuildTargetPlatform Win64        = FindOrAddByName(Tag.Platform.Win64); // 64-bit Windows
		public static BuildTargetPlatform HoloLens     = FindOrAddByName(Tag.Platform.HoloLens);
		public static BuildTargetPlatform Mac          = FindOrAddByName(Tag.Platform.Mac);
		public static BuildTargetPlatform XboxOne      = FindOrAddByName(Tag.Platform.XboxOne);
		public static BuildTargetPlatform PS4          = FindOrAddByName(Tag.Platform.PS4);
		public static BuildTargetPlatform PS5          = FindOrAddByName(Tag.Platform.PS5);
		public static BuildTargetPlatform IOS          = FindOrAddByName(Tag.Platform.IOS);
		public static BuildTargetPlatform Android      = FindOrAddByName(Tag.Platform.Android);
		public static BuildTargetPlatform HTML5        = FindOrAddByName(Tag.Platform.HTML5);
		public static BuildTargetPlatform Linux        = FindOrAddByName(Tag.Platform.Linux);
		public static BuildTargetPlatform LinuxAArch64 = FindOrAddByName(Tag.Platform.LinuxAArch64);
		public static BuildTargetPlatform AllDesktop   = FindOrAddByName(Tag.Platform.AllDesktop);

		public static BuildTargetPlatform TVOS   = FindOrAddByName(Tag.Platform.TVOS);
		public static BuildTargetPlatform Switch = FindOrAddByName(Tag.Platform.Switch);
		public static BuildTargetPlatform Lumin  = FindOrAddByName(Tag.Platform.Lumin); // Confidential platform
	}

	internal class BuildTargetPlatformTypeConverter : TypeConverter
	{
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) 
			=> sourceType == typeof(string) ||
               base.CanConvertFrom(context, sourceType);

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) 
			=> destinationType == typeof(string) ||
                base.CanConvertTo(context, destinationType);

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) => value.GetType() == typeof(string) ? BuildTargetPlatform.Parse((string)value) : base.ConvertFrom(context, culture, value);

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			if (destinationType == typeof(string))
			{
				BuildTargetPlatform Platform = (BuildTargetPlatform)value;
				return Platform.ToString();
			}
			return base.ConvertTo(context, culture, value, destinationType);
		}
	}

    // Extension methods used for serializing argetPlatform instances
    internal static class TargetPlatformExtensionMethods
	{
		// Read an TargetPlatform instance from a binary archive
		public static BuildTargetPlatform ReadTargetPlatform(this BinaryArchiveReader Reader)
		{
			return BuildTargetPlatform.Parse(Reader.ReadString());
		}

		// Write an TargetPlatform instance to a binary archive
		public static void WriteTargetPlatform(this BinaryArchiveWriter Writer, BuildTargetPlatform PlatformToWrite)
		{
			Writer.WriteString(PlatformToWrite.ToString());
		}
	}

	// Platform groups
	public struct BuildPlatformGroup
	{
		#region Private/boilerplate
		private readonly int Id; // internal concrete name of the group

		// shared string instance registry - pass in a delegate to create a new one with a name that wasn't made yet
		private static UniqueStringRegistry StringRegistry;

		private static UniqueStringRegistry GetUniqueStringRegistry()
		{
			if (StringRegistry == null)
			{
				StringRegistry = new UniqueStringRegistry();
			}
			return StringRegistry;
		}

        public BuildPlatformGroup(string Name) 
			=> Id = GetUniqueStringRegistry().FindOrAddByName(Name);

        private BuildPlatformGroup(int InId) 
			=> Id = InId;

#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable IDE0060 // Remove unused parameter
		public void GetObjectData(SerializationInfo Info, StreamingContext Context) 
			=> Info.AddValue("Name", ToString());

		public BuildPlatformGroup(SerializationInfo Info, StreamingContext Context) 
			=> Id = GetUniqueStringRegistry().FindOrAddByName((string)Info.GetValue("Name", typeof(string)));
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore IDE0079 // Remove unnecessary suppression

		static private BuildPlatformGroup FindOrAddByName(string Name) 
			=> new BuildPlatformGroup(GetUniqueStringRegistry().FindOrAddByName(Name));
		
		public static bool operator ==(BuildPlatformGroup A, BuildPlatformGroup B) 
			=> A.Id == B.Id;

		public static bool operator !=(BuildPlatformGroup A, BuildPlatformGroup B) 
			=> A.Id != B.Id;

		public override bool Equals(object B) 
			=> Id == ((BuildPlatformGroup)B).Id;

		public override int GetHashCode() => Id;

		#endregion

		public override string ToString() 
			=> GetUniqueStringRegistry().GetStringForId(Id);

		public static BuildPlatformGroup[] GetValidGroups() 
			=> Array.ConvertAll(GetUniqueStringRegistry().GetStringIds(), x => new BuildPlatformGroup(x));

		public static string[] GetValidGroupNames() 
			=> GetUniqueStringRegistry().GetStringNames();

		public static bool IsValidName(string Name) 
			=> GetUniqueStringRegistry().HasString(Name);

		// this group is just to lump Win32 and Win64 into Windows directories, removing the special Windows logic in MakeListOfUnsupportedPlatforms
		public static BuildPlatformGroup Windows = FindOrAddByName(Tag.PlatformGroup.Windows);

		// this group is just to lump Platform directories
		public static BuildPlatformGroup HoloLens   = FindOrAddByName(Tag.PlatformGroup.HoloLens);
		public static BuildPlatformGroup Microsoft  = FindOrAddByName(Tag.PlatformGroup.Microsoft);
		public static BuildPlatformGroup Apple      = FindOrAddByName(Tag.PlatformGroup.Apple);
		public static BuildPlatformGroup IOS        = FindOrAddByName(Tag.PlatformGroup.IOS);
		public static BuildPlatformGroup Unix       = FindOrAddByName(Tag.PlatformGroup.Unix);
		public static BuildPlatformGroup Linux      = FindOrAddByName(Tag.PlatformGroup.Linux);
		public static BuildPlatformGroup Android    = FindOrAddByName(Tag.PlatformGroup.Android);
		public static BuildPlatformGroup Sony       = FindOrAddByName(Tag.PlatformGroup.Sony);
		public static BuildPlatformGroup XboxCommon = FindOrAddByName(Tag.PlatformGroup.XboxCommon);
		public static BuildPlatformGroup AllDesktop = FindOrAddByName(Tag.PlatformGroup.AllDesktop);
		public static BuildPlatformGroup Desktop    = FindOrAddByName(Tag.PlatformGroup.Desktop);
	}

	// The class of platform. See Utils.GetPlatformsInClass().
	public enum BuildPlatformClass
	{
		All,     // All platforms
		Desktop, // All desktop platforms (Win32, Win64, Mac, Linux)
		Editor,  // All platforms which support the editor (Win64, Mac, Linux)
		Server,  // Platforms which support running servers (Win32, Win64, Mac, Linux)
	}

	// The type of configuration a target can be built for
	public enum TargetConfiguration
	{
		Unknown,
		Debug,
		DebugGame, // equivalent to development, but with optimization disabled for game modules
		Development,
		Shipping,
		Test,
	}
}
