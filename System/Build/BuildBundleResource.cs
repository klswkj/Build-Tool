namespace BuildTool
{
	public sealed class BuildBundleResource
	{
		public BuildBundleResource(ModuleRules.BundleResource BundleResource)
		{
			ResourcePath = BundleResource.ResourcePath;
			BundleContentsSubdir = BundleResource.BundleContentsSubdir;
			bShouldLog = BundleResource.bShouldLog;
		}

		public string ResourcePath = null;
		public string BundleContentsSubdir = null;
		public bool bShouldLog = true;
	}
}
