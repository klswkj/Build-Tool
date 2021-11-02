using BuildToolUtilities;
// # Don't erase 'using ($namespace);'.

namespace BuildTool
{
	// TargetRules 만들때 필요.
	// TargetInfo => TargetRulesInfo
	// Information about a target, passed along when creating a module descriptor
	public class TargetInfo
	{
		public readonly string Name; // Name of the target being built
		public readonly BuildTargetPlatform Platform; // The platform that the target is being built for
		public readonly TargetConfiguration Configuration; // The configuration being built
		public readonly string Architecture; // Architecture that the target is being built for (or an empty string for the default)
		public readonly FileReference ProjectFile; // The project containing the target

		public ReadOnlyBuildVersion Version => ReadOnlyBuildVersion.Current;
		public CommandLineArguments ExtraArguments;

		public TargetInfo(string InName, BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, string InArchitecture, FileReference InProjectFile, CommandLineArguments Arguments)
		{
			this.Name = InName;
			this.Platform = InPlatform;
			this.Configuration = InConfiguration;
			this.Architecture = InArchitecture;
			this.ProjectFile = InProjectFile;
			this.ExtraArguments = Arguments;
		}
	}
}
