using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Enumerates build action types.
	enum ActionType
	{
		BuildProject,
		Compile,
		CreateAppBundle,
		GenerateDebugInfo,
		Link,
		WriteMetadata,
		PostBuildStep,
		ParseTimingInfo,
	}

	// A build action.
	// Preparation and Assembly (serialized)
	class Action
	{
		// The type of this action (for debugging purposes).
		public ActionType Type;

		// Every file this action depends on.
		// These files need to exist and be up to date in order for this action to even be considered
		public List<FileItem> PrerequisiteItems = new List<FileItem>();

		// The files that this action produces after completing
		public List<FileItem> ProducedItems = new List<FileItem>();

		// Items that should be deleted before running this action
		public List<FileItem> DeleteItems = new List<FileItem>();

		// For C++ source files, specifies a dependency list file used to check changes to header files
		public FileItem DependencyListFile;

		// For C++ source files, specifies a timing file used to track timing information.
		public FileItem TimingFile;

		// Set of other actions that this action depends on. This set is built when the action graph is linked.
		public HashSet<Action> PrerequisiteActions;

		public DirectoryReference WorkingDirectory = null; // Directory from which to execute the program to create produced items
		public FileReference      CommandPath      = null; // The command to run to create produced items
		public string             CommandArguments = null; // Command-line parameters to pass to the program

		public bool bPrintDebugInfo = false; // True if we should log extra information when we run a program to create produced items

		// Optional friendly description of the type of command being performed,
		// for example "Compile" or "Link".  Displayed by some executors.
		public string CommandDescription = null;

		// Human-readable description of this action that may be displayed as status while invoking the action.
		// This is often the name of the file being compiled, or an executable file name being linked.  Displayed by some executors.
		public string StatusDescription = "...";

		// If set, will be output whenever the group differs to the last executed action. Set when executing multiple targets at once.
		public List<string> GroupNames = new List<string>();

		// True if this action is allowed to be run on a remote machine when a distributed build system is being used, such as XGE
		public bool bCanExecuteRemotely = false;

		// True if this action is allowed to be run on a remote machine with SNDBS.
		// Files with #import directives must be compiled locally. Also requires bCanExecuteRemotely = true.
		public bool bCanExecuteRemotelyWithSNDBS = true;

		// True if this action is using the GCC compiler.
		// Some build systems may be able to optimize for this case.
		public bool bIsGCCCompiler = false;

		// Whether we should log this action, whether executed locally or remotely.
		// This is useful for actions that take time but invoke tools without any console output.
		public bool bShouldOutputStatusDescription = true;

		// True if any libraries produced by this action should be considered 'import libraries'
		public bool bProducesImportLibrary = false;

		// Preparation only (not serialized)
		// Total number of actions depending on this one.
		public int NumTotalDependentActions = 0;

		// Assembly only (not serialized)
		// Start time of action, optionally set by executor.
		public DateTimeOffset StartTime = DateTimeOffset.MinValue;

		// End time of action, optionally set by executor.
		public DateTimeOffset EndTime = DateTimeOffset.MinValue;

		public Action(ActionType InActionType)
		{
			Type = InActionType;

			// link actions are going to run locally on SN-DBS so don't try to distribute them as that generates warnings for missing tool templates
			if ( Type == ActionType.Link )
			{
				bCanExecuteRemotelyWithSNDBS = false;
			}
		}

		// # Keep Sync with Read between Write.
		public Action(BinaryArchiveReader Reader)
		{
			Type                           = (ActionType)Reader.ReadByte();
			WorkingDirectory               = Reader.ReadDirectoryReference();
			bPrintDebugInfo                = Reader.ReadBool();
			CommandPath                    = Reader.ReadFileReference();
			CommandArguments               = Reader.ReadString();
			CommandDescription             = Reader.ReadString();
			StatusDescription              = Reader.ReadString();
			bCanExecuteRemotely            = Reader.ReadBool();
			bCanExecuteRemotelyWithSNDBS   = Reader.ReadBool();
			bIsGCCCompiler                 = Reader.ReadBool();
			bShouldOutputStatusDescription = Reader.ReadBool();
			bProducesImportLibrary         = Reader.ReadBool();
			PrerequisiteItems              = Reader.ReadList(() => Reader.ReadFileItem());
			ProducedItems                  = Reader.ReadList(() => Reader.ReadFileItem());
			DeleteItems                    = Reader.ReadList(() => Reader.ReadFileItem());
			DependencyListFile             = Reader.ReadFileItem();
		}

		// # Keep Sync with Read between Write.
		// ISerializable: Called when serialized to report additional properties that should be saved
		public void Write(BinaryArchiveWriter Writer)
		{
			Writer.WriteByte((byte)Type);
			Writer.WriteDirectoryReference(WorkingDirectory);
			Writer.WriteBool(bPrintDebugInfo);
			Writer.WriteFileReference(CommandPath);
			Writer.WriteString(CommandArguments);
			Writer.WriteString(CommandDescription);
			Writer.WriteString(StatusDescription);
			Writer.WriteBool(bCanExecuteRemotely);
			Writer.WriteBool(bCanExecuteRemotelyWithSNDBS);
			Writer.WriteBool(bIsGCCCompiler);
			Writer.WriteBool(bShouldOutputStatusDescription);
			Writer.WriteBool(bProducesImportLibrary);
			Writer.WriteList(PrerequisiteItems, Item => Writer.WriteFileItem(Item));
			Writer.WriteList(ProducedItems, Item => Writer.WriteFileItem(Item));
			Writer.WriteList(DeleteItems, Item => Writer.WriteFileItem(Item));
			Writer.WriteFileItem(DependencyListFile);
		}

		// Writes an action to a json file
		public static Action ImportJson(JsonObject ObjectToParse)
		{
			// Action Action = new Action(Object.GetEnumField<ActionType>("Type"));
			Action OutAction = new Action(ObjectToParse.GetEnumField<ActionType>(nameof(ActionType)));

			if (ObjectToParse.TryGetStringField(nameof(Action.WorkingDirectory), out string WorkingDirectory))
            {
                OutAction.WorkingDirectory = new DirectoryReference(WorkingDirectory);
            }

            if (ObjectToParse.TryGetStringField(nameof(Action.CommandPath), out string CommandPath))
            {
                OutAction.CommandPath = new FileReference(CommandPath);
            }

            if (ObjectToParse.TryGetStringField(nameof(Action.CommandArguments), out string CommandArguments))
            {
                OutAction.CommandArguments = CommandArguments;
            }

            if (ObjectToParse.TryGetStringField(nameof(Action.CommandDescription), out string CommandDescription))
            {
                OutAction.CommandDescription = CommandDescription;
            }

            if (ObjectToParse.TryGetStringField(nameof(Action.StatusDescription), out string StatusDescription))
            {
                OutAction.StatusDescription = StatusDescription;
            }

            if (ObjectToParse.TryGetBoolField(nameof(Action.bPrintDebugInfo), out bool bPrintDebugInfo))
            {
                OutAction.bPrintDebugInfo = bPrintDebugInfo;
            }

            if (ObjectToParse.TryGetBoolField(nameof(Action.bCanExecuteRemotely), out bool bCanExecuteRemotely))
            {
                OutAction.bCanExecuteRemotely = bCanExecuteRemotely;
            }

            if (ObjectToParse.TryGetBoolField(nameof(Action.bCanExecuteRemotelyWithSNDBS), out bool bCanExecuteRemotelyWithSNDBS))
            {
                OutAction.bCanExecuteRemotelyWithSNDBS = bCanExecuteRemotelyWithSNDBS;
            }

            if (ObjectToParse.TryGetBoolField(nameof(Action.bIsGCCCompiler), out bool bIsGCCCompiler))
            {
                OutAction.bIsGCCCompiler = bIsGCCCompiler;
            }

            if (ObjectToParse.TryGetBoolField(nameof(Action.bShouldOutputStatusDescription), out bool bShouldOutputStatusDescription))
            {
                OutAction.bShouldOutputStatusDescription = bShouldOutputStatusDescription;
            }

            if (ObjectToParse.TryGetBoolField(nameof(Action.bProducesImportLibrary), out bool bProducesImportLibrary))
            {
                OutAction.bProducesImportLibrary = bProducesImportLibrary;
            }

            if (ObjectToParse.TryGetStringArrayField(nameof(Action.PrerequisiteItems), out string[] PrerequisiteItems))
            {
                OutAction.PrerequisiteItems.AddRange(PrerequisiteItems.Select(x => FileItem.GetItemByPath(x)));
            }

            if (ObjectToParse.TryGetStringArrayField(nameof(Action.ProducedItems), out string[] ProducedItems))
            {
                OutAction.ProducedItems.AddRange(ProducedItems.Select(x => FileItem.GetItemByPath(x)));
            }

            if (ObjectToParse.TryGetStringArrayField(nameof(Action.DeleteItems), out string[] DeleteItems))
            {
                OutAction.DeleteItems.AddRange(DeleteItems.Select(x => FileItem.GetItemByPath(x)));
            }

            if (ObjectToParse.TryGetStringField(nameof(Action.DependencyListFile), out string DependencyListFile))
            {
                OutAction.DependencyListFile = FileItem.GetItemByPath(DependencyListFile);
            }

            return OutAction;
		}

		// Writes an action to a json file
		public void ExportJson(JsonWriter Writer)
		{
			Writer.WriteEnumValue(nameof(Action.Type), Type);
			Writer.WriteValue(nameof(Action.WorkingDirectory), WorkingDirectory.FullName);
			Writer.WriteValue(nameof(Action.CommandPath), CommandPath.FullName);
			Writer.WriteValue(nameof(Action.CommandArguments), CommandArguments);
			Writer.WriteValue(nameof(Action.CommandDescription), CommandDescription);
			Writer.WriteValue(nameof(Action.StatusDescription), StatusDescription);
			Writer.WriteValue(nameof(Action.bPrintDebugInfo), bPrintDebugInfo);
			Writer.WriteValue(nameof(Action.bCanExecuteRemotely), bCanExecuteRemotely);
			Writer.WriteValue(nameof(Action.bCanExecuteRemotelyWithSNDBS), bCanExecuteRemotelyWithSNDBS);
			Writer.WriteValue(nameof(Action.bIsGCCCompiler), bIsGCCCompiler);
			Writer.WriteValue(nameof(Action.bShouldOutputStatusDescription), bShouldOutputStatusDescription);
			Writer.WriteValue(nameof(Action.bProducesImportLibrary), bProducesImportLibrary);

			Writer.WriteArrayStart(nameof(Action.PrerequisiteItems));
			foreach(FileItem PrerequisiteItem in PrerequisiteItems)
			{
				Writer.WriteValue(PrerequisiteItem.AbsolutePath);
			}
			Writer.WriteArrayEnd();

			Writer.WriteArrayStart(nameof(Action.ProducedItems));
			foreach(FileItem ProducedItem in ProducedItems)
			{
				Writer.WriteValue(ProducedItem.AbsolutePath);
			}
			Writer.WriteArrayEnd();

			Writer.WriteArrayStart(nameof(Action.DeleteItems));
			foreach(FileItem DeleteItem in DeleteItems)
			{
				Writer.WriteValue(DeleteItem.AbsolutePath);
			}
			Writer.WriteArrayEnd();

			if (DependencyListFile != null)
			{
				Writer.WriteValue(nameof(Action.DependencyListFile), DependencyListFile.AbsolutePath);
			}
		}

		// Finds conflicts betwee two actions, and prints them to the log
		// <param name="Other">Other action to compare to.</param>
		// <returns>True if any conflicts were found, false otherwise.</returns>
		public bool CheckForConflicts(Action Other)
		{
			bool bResult = true;
			if(Type != Other.Type)
			{
				LogConflict("action type is different", Type.ToString(), Other.Type.ToString());
				bResult = false;
			}
			if(!Enumerable.SequenceEqual(PrerequisiteItems, Other.PrerequisiteItems))
			{
				LogConflict("prerequisites are different", String.Join(", ", PrerequisiteItems.Select(x => x.FileDirectory)), String.Join(", ", Other.PrerequisiteItems.Select(x => x.FileDirectory)));
				bResult = false;
			}
			if(!Enumerable.SequenceEqual(DeleteItems, Other.DeleteItems))
			{
				LogConflict("deleted items are different", String.Join(", ", DeleteItems.Select(x => x.FileDirectory)), String.Join(", ", Other.DeleteItems.Select(x => x.FileDirectory)));
				bResult = false;
			}
			if(DependencyListFile != Other.DependencyListFile)
			{
				LogConflict("dependency list is different", (DependencyListFile == null)? "(none)" : DependencyListFile.AbsolutePath, (Other.DependencyListFile == null)? "(none)" : Other.DependencyListFile.AbsolutePath);
				bResult = false;
			}
			if(WorkingDirectory != Other.WorkingDirectory)
			{
				LogConflict("working directory is different", WorkingDirectory.FullName, Other.WorkingDirectory.FullName);
				bResult = false;
			}
			if(CommandPath != Other.CommandPath)
			{
				LogConflict("command path is different", CommandPath.FullName, Other.CommandPath.FullName);
				bResult = false;
			}
			if(CommandArguments != Other.CommandArguments)
			{
				LogConflict("command arguments are different", CommandArguments, Other.CommandArguments);
				bResult = false;
			}
			return bResult;
		}

		// Adds the description of a merge error to an output message
		// <param name="Description">Description of the difference</param>
		// <param name="OldValue">Previous value for the field</param>
		// <param name="NewValue">Conflicting value for the field</param>
		void LogConflict(string Description, string OldValue, string NewValue)
		{
			Log.TraceError("Unable to merge actions producing {0}: {1}", ProducedItems[0].FileDirectory.GetFileName(), Description);
			Log.TraceLog("  Previous: {0}", OldValue);
			Log.TraceLog("  Conflict: {0}", NewValue);
		}

		// Increment the number of dependents, recursively
		// <param name="VisitedActions">Set of visited actions</param>
		public void IncrementDependentCount(HashSet<Action> VisitedActions)
		{
			if(VisitedActions.Add(this))
			{
				NumTotalDependentActions++;
				foreach(Action PrerequisiteAction in PrerequisiteActions)
				{
					PrerequisiteAction.IncrementDependentCount(VisitedActions);
				}
			}
		}

		// Compares two actions based on total number of dependent items, descending.
		public static int Compare(Action A, Action B)
		{
			// Primary sort criteria is total number of dependent files, up to max depth.
			if (B.NumTotalDependentActions != A.NumTotalDependentActions)
			{
				return Math.Sign(B.NumTotalDependentActions - A.NumTotalDependentActions);
			}
			// Secondary sort criteria is number of pre-requisites.
			else
			{
				return Math.Sign(B.PrerequisiteItems.Count - A.PrerequisiteItems.Count);
			}
		}

		public override string ToString()
		{
			string ReturnString = "";
			if (CommandPath != null)
			{
				ReturnString += CommandPath + " - ";
			}
			if (CommandArguments != null)
			{
				ReturnString += CommandArguments;
			}
			return ReturnString;
		}

		// Returns the amount of time that this action is or has been executing in.
		public TimeSpan Duration
		{
			get
			{
				if (EndTime == DateTimeOffset.MinValue)
				{
					return DateTimeOffset.Now - StartTime;
				}

				return EndTime - StartTime;
			}
		}
	}
}
