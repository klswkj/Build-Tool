namespace BuildTool
{
	// Factory class for registering platforms at startup
	internal abstract class BuildPlatformFactory
	{
		// Gets the target platform for an individual factory
		public abstract BuildTargetPlatform TargetPlatform { get; }

		// Register the platform with the UEBuildPlatform class
		public abstract void RegisterBuildPlatforms();
	}
}
