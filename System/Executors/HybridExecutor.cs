using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{	
	// Executor which distributes large sets of parallel actions through a remote executor, 
	// and any leaf serial actions through a local executor. 
	// Currently uses XGE for remote actions and ParallelExecutor for local actions.
	
	internal sealed class HybridExecutor : ActionExecutor
	{
		// Maximum number of actions to execute locally.
		[XMLConfigFile(Category = "HybridExecutor")]
		readonly int MaxLocalActions;

		private readonly ActionExecutor RemoteExecutor; // Executor to use for remote actions
		private readonly ActionExecutor LocalExecutor; // Executor to use for local actions

		public HybridExecutor(int InMaxLocalActions)
		{
			MaxLocalActions = InMaxLocalActions;
			LocalExecutor  = new ParallelExecutor(MaxLocalActions);
			RemoteExecutor = new XGE();

			XMLConfig.ApplyTo(this);

			if(MaxLocalActions == 0)
			{
				MaxLocalActions = Utils.GetPhysicalProcessorCount();
			}
		}

        // Tests whether this executor can be used
        public static bool IsAvailable() => XGE.IsAvailable() && ParallelExecutor.IsAvailable();

        // Name of this executor for diagnostic output
        public override string OutputName 
			=> String.Format("hybrid ({0}+{1})", LocalExecutor.OutputName, RemoteExecutor.OutputName);

		// Execute the given actions
		public override bool ExecuteActions(List<Action> ActionsToExecute, bool bLogDetailedActionStats)
		{
			// Find the number of dependants for each action
			Dictionary<Action, int> ActionToNumDependents = ActionsToExecute.ToDictionary(x => x, x => 0);
			foreach(Action Action in ActionsToExecute)
			{
				foreach(Action PrerequisiteAction in Action.PrerequisiteActions)
				{
					++ActionToNumDependents[PrerequisiteAction];
				}
			}

			// Build up a set of leaf actions in several iterations, ensuring that the number of leaf actions in each 
			HashSet<Action> LeafActions = new HashSet<Action>();
			for(;;)
			{
				// Find all the leaf actions in the graph
				List<Action> NewLeafActions = new List<Action>();
				foreach(Action Action in ActionsToExecute)
				{
					if(ActionToNumDependents[Action] == 0 && !LeafActions.Contains(Action))
					{
						NewLeafActions.Add(Action);
					}
				}

				// Exit once we can't prune any more layers from the tree
				if(NewLeafActions.Count == 0 || 
					MaxLocalActions <= NewLeafActions.Count)
				{
					break;
				}

				// Add these actions to the set of leaf actions
				LeafActions.UnionWith(NewLeafActions);

				// Decrement the dependent counts for any of their prerequisites, so we can try and remove those from the tree in another iteration
				foreach(Action NewLeafAction in NewLeafActions)
				{
					foreach(Action PrerequisiteAction in NewLeafAction.PrerequisiteActions)
					{
						--ActionToNumDependents[PrerequisiteAction];
					}
				}
			}

			// Split the list of actions into those which should be executed locally and remotely
			List<Action> LocalActionsToExecute  = new List<Action>(LeafActions.Count);
			List<Action> RemoteActionsToExecute = new List<Action>(ActionsToExecute.Count - LeafActions.Count);

			foreach(Action ActionToExecute in ActionsToExecute)
			{
				if(LeafActions.Contains(ActionToExecute))
				{
					LocalActionsToExecute.Add(ActionToExecute);
				}
				else
				{
					RemoteActionsToExecute.Add(ActionToExecute);
				}
			}

			// Execute the remote actions
			if(0 < RemoteActionsToExecute.Count)
			{
				if (!RemoteExecutor.ExecuteActions(RemoteActionsToExecute, bLogDetailedActionStats))
				{
					return false;
				}
			}

			// Pass all the local actions through to the parallel executor
			if(0 < LocalActionsToExecute.Count)
			{
				if(!LocalExecutor.ExecuteActions(LocalActionsToExecute, bLogDetailedActionStats))
				{
					return false;
				}
			}

			return true;
		}
	}
}
