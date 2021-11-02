using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Stores custom build steps to be executed by a project or plugin
	// Keep Sync with .uplugin files, In PROJECTS_API, FProjectDescriptor.h/cpp FModuleDescriptor.h/cpp, FPluginsDescriptor.h/cpp
	public sealed class CustomBuildSteps
	{
		private readonly Dictionary<BuildTargetPlatform, string[]> HostPlatformToCommands = new Dictionary<BuildTargetPlatform,string[]>();

		// Construct a custom build steps object from a Json object.
		public CustomBuildSteps(JsonObject RawObject)
		{
			foreach(string HostPlatformName in RawObject.KeyNames)
			{
				if (BuildTargetPlatform.TryParse(HostPlatformName, out BuildTargetPlatform Platform))
				{
					HostPlatformToCommands.Add(Platform, RawObject.GetStringArrayField(HostPlatformName));
				}
			}
		}

		// Reads a list of build steps from a Json project or plugin descriptor
		public static bool TryRead(JsonObject RawJSONDescriptorObject, string FieldNameToRead, out CustomBuildSteps OutBuildSteps)
		{
			if (RawJSONDescriptorObject.TryGetObjectField(FieldNameToRead, out JsonObject BuildStepsObject))
			{
				OutBuildSteps = new CustomBuildSteps(BuildStepsObject);
				return true;
			}
			else
			{
				OutBuildSteps = null;
				return false;
			}
		}

		// Reads a list of build steps from a Json project or plugin descriptor
		public void Write(JsonWriter WriterToReceiveJSONOutput, string FieldNameToRead)
		{
			WriterToReceiveJSONOutput.WriteObjectStart(FieldNameToRead);
			foreach(KeyValuePair<BuildTargetPlatform, string[]> Pair in HostPlatformToCommands.OrderBy(x => x.Key.ToString()))
			{
				WriterToReceiveJSONOutput.WriteArrayStart(Pair.Key.ToString());
				foreach(string Line in Pair.Value)
				{
					WriterToReceiveJSONOutput.WriteValue(Line);
				}
				WriterToReceiveJSONOutput.WriteArrayEnd();
			}
			WriterToReceiveJSONOutput.WriteObjectEnd();
		}

		// Tries to get the commands for a given host platform
		public bool TryGetCommands(BuildTargetPlatform HostPlatformToLookFor, out string[] OutCommands)
		{
			if (HostPlatformToCommands.TryGetValue(HostPlatformToLookFor, out string[] Commands) && 
				0 < Commands.Length)
			{
				OutCommands = Commands;
				return true;
			}
			else
			{
				OutCommands = null;
				return false;
			}
		}
	}
}
