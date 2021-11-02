namespace BuildTool
{
	// Base class to handle deploy of a target for a given platform
	internal abstract class BuildDeploy
	{
		// Prepare the target for deployment
		// <param name="Receipt">Receipt for the target being deployed</param>
		// <returns>True if successful, false if not</returns>
		public virtual bool PrepTargetForDeployment(TargetReceipt Receipt)
		{
			return true;
		}
	}
}
