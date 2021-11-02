using System;
using System.Collections.Generic;
using System.Reflection;
using BuildToolUtilities;

namespace BuildTool
{
	// Interface for toolchain operations that produce output
	interface IActionGraphBuilder
	{
		// Creates a new action to be built as part of this target
		Action CreateAction(ActionType InActionType);

		// Creates a response file for use in the action graph
		FileItem CreateIntermediateTextFile(FileReference ReponseFile, string Contents);

		// Adds a file which is in the non-unity working set
		void AddFileToWorkingSet(FileItem FileToAddToWorkingSet);

		// Adds a file which is a candidate for being in the non-unity working set
		void AddCandidateForWorkingSet(FileItem FileToAddToWorkingSet);

		// Adds a source directory. These folders are scanned recursively for C++ source files.
		void AddSourceDir(DirectoryItem CPPSourceDir);

		// Adds the given source files as dependencies
		void AddSourceFiles(DirectoryItem SourceDirToBuild, FileItem[] SourceFiles);

		// Sets the output items which belong to a particular module
		void SetOutputItemsForModule(string ModuleName, FileItem[] OutputItemsForThisModule);

		// Adds a diagnostic message
		void AddDiagnostic(string MessageToDisplay);
	}

	// Implementation of IActionGraphBuilder which discards all unnecessary operations
	class NullActionGraphBuilder : IActionGraphBuilder
	{
		public Action CreateAction(ActionType Type)
		{
			return new Action(Type);
		}

		public virtual FileItem CreateIntermediateTextFile(FileReference Location, string Contents)
		{
			StringUtils.WriteFileIfChanged(Location, Contents, StringComparison.OrdinalIgnoreCase);
			return FileItem.GetItemByFileReference(Location);
		}

		public virtual void AddSourceDir(DirectoryItem SourceDir)
		{
		}

		public virtual void AddSourceFiles(DirectoryItem SourceDir, FileItem[] SourceFiles)
		{
		}

		public virtual void AddFileToWorkingSet(FileItem File)
		{
		}

		public virtual void AddCandidateForWorkingSet(FileItem File)
		{
		}

		public virtual void AddDiagnostic(string Message)
		{
		}

		public virtual void SetOutputItemsForModule(string ModuleName, FileItem[] OutputItems)
		{
		}
	}

	// Implementation of IActionGraphBuilder which forwards calls to an underlying implementation,
	// allowing derived classes to intercept certain calls
	class ForwardingActionGraphBuilder : IActionGraphBuilder
	{
		private readonly IActionGraphBuilder Inner;

		public ForwardingActionGraphBuilder(IActionGraphBuilder Inner)
		{
			this.Inner = Inner;
		}

		public virtual Action CreateAction(ActionType Type)
		{
			return Inner.CreateAction(Type);
		}

		public virtual FileItem CreateIntermediateTextFile(FileReference Location, string Contents)
		{
			return Inner.CreateIntermediateTextFile(Location, Contents);
		}

		public virtual void AddSourceDir(DirectoryItem SourceDir)
		{
			Inner.AddSourceDir(SourceDir);
		}

		public virtual void AddSourceFiles(DirectoryItem SourceDir, FileItem[] SourceFiles)
		{
			Inner.AddSourceFiles(SourceDir, SourceFiles);
		}

		public virtual void AddFileToWorkingSet(FileItem File)
		{
			Inner.AddFileToWorkingSet(File);
		}

		public virtual void AddCandidateForWorkingSet(FileItem File)
		{
			Inner.AddCandidateForWorkingSet(File);
		}

		public virtual void AddDiagnostic(string Message)
		{
			Inner.AddDiagnostic(Message);
		}

		public virtual void SetOutputItemsForModule(string ModuleName, FileItem[] OutputItems)
		{
			Inner.SetOutputItemsForModule(ModuleName, OutputItems);
		}
	}

    // Extension methods for IActionGraphBuilder classes
    internal static class ActionGraphBuilderExtensions
	{
		// Creates an action which copies a file from one location to another
		public static Action CreateCopyAction(this IActionGraphBuilder Graph, FileItem SourceFile, FileItem TargetFile)
		{
			Action CopyAction = Graph.CreateAction(ActionType.BuildProject);

			CopyAction.CommandDescription  = Tag.ActionDescriptor.Copy;
			CopyAction.CommandPath         = BuildHostPlatform.Current.ShellPath;
			CopyAction.WorkingDirectory    = BuildTool.EngineSourceDirectory;
			CopyAction.StatusDescription   = TargetFile.FileDirectory.GetFileName();
			CopyAction.bCanExecuteRemotely = false;
			CopyAction.PrerequisiteItems.Add(SourceFile);
			CopyAction.ProducedItems.Add(TargetFile);
			CopyAction.DeleteItems.Add(TargetFile);

			if (BuildHostPlatform.Current.ShellType == ShellType.Cmd)
			{
				CopyAction.CommandArguments = String.Format("/C \"copy /Y \"{0}\" \"{1}\" 1>nul\"", SourceFile.AbsolutePath, TargetFile.AbsolutePath);
			}
			else
			{
				CopyAction.CommandArguments = String.Format("-c 'cp -f \"{0}\" \"{1}\"'", SourceFile.AbsolutePath, TargetFile.AbsolutePath);
			}

			return CopyAction;
		}

		// Creates an action which copies a file from one location to another
		public static FileItem CreateCopyAction(this IActionGraphBuilder Graph, FileReference SourceFile, FileReference TargetFile)
		{
			FileItem SourceFileItem = FileItem.GetItemByFileReference(SourceFile);
			FileItem TargetFileItem = FileItem.GetItemByFileReference(TargetFile);

			Graph.CreateCopyAction(SourceFileItem, TargetFileItem);

			return TargetFileItem;
		}

		public static Action CreateRecursiveAction<T>(this IActionGraphBuilder Graph, ActionType Type, string Arguments) where T : ToolMode
		{
			ToolModeAttribute Attribute = typeof(T).GetCustomAttribute<ToolModeAttribute>();
			if (Attribute == null)
			{
				throw new BuildException("Missing ToolModeAttribute on {0}", typeof(T).Name);
			}
#warning(Should make -mode= to tag)
			Action NewAction = Graph.CreateAction(Type);
			NewAction.CommandPath = BuildTool.GetBuildToolAssemblyPath();
			NewAction.CommandArguments = String.Format("-Mode={0} {1}", Attribute.ToolModeName, Arguments);
			return NewAction;
		}

		// Creates a text file with the given contents.
		// If the contents of the text file aren't changed, it won't write the new contents to
		// the file to avoid causing an action to be considered outdated.
		// <param name="Graph">The action graph</param>
		// <param name="AbsolutePath">Path to the intermediate file to create</param>
		// <param name="Contents">Contents of the new file</param>
		// <returns>File item for the newly created file</returns>
		public static FileItem CreateIntermediateTextFile(this IActionGraphBuilder Graph, FileReference AbsolutePath, IEnumerable<string> Contents)
		{
			return Graph.CreateIntermediateTextFile(AbsolutePath, string.Join(Environment.NewLine, Contents));
		}
	}
}
