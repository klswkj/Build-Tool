using System.Collections.Generic;

namespace BuildTool
{
	abstract class ActionExecutor
	{
		public abstract string OutputName { get; }

		public abstract bool ExecuteActions(List<Action> ActionsToExecute, bool bLogDetailedActionStats);
	}

}
