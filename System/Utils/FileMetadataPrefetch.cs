using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BuildToolUtilities;

namespace BuildTool
{
	// Prefetches metadata from the filesystem,
	// by populating FileItem and DirectoryItem objects for requested directory trees.
	internal static class FileMetadataPrefetch
	{
		private static readonly ThreadPoolWorkQueue     ThreadWorkerQueue = new ThreadPoolWorkQueue();     // Queue for tasks added to the thread pool

		private static readonly CancellationTokenSource CancelSource = new CancellationTokenSource(); // Used to cancel any queued tasks
		private static readonly CancellationToken       CancelToken  = CancelSource.Token; // The cancellation token

		// Set of all the directory trees that have been queued up, to save adding any more than once.
		readonly static HashSet<DirectoryReference> QueuedDirectories = new HashSet<DirectoryReference>();

		// Enqueue the engine directory for prefetching
		public static void QueueEngineDirectory()
		{
			lock(QueuedDirectories)
			{
				if(QueuedDirectories.Add(BuildTool.EngineDirectory))
				{
					Enqueue(() => ScanEngineDirectory());
				}
			}
		}

		// Enqueue a project directory for prefetching
		public static void QueueProjectDirectory(DirectoryReference ProjectDirectory)
		{
			lock(QueuedDirectories)
			{
				if(QueuedDirectories.Add(ProjectDirectory))
				{
					Enqueue(() => ScanProjectDirectory(DirectoryItem.GetItemByDirectoryReference(ProjectDirectory)));
				}
			}
		}

		// Enqueue a directory tree for prefetching
		// <param name="Directory">Directory to start searching from</param>
		public static void QueueDirectoryTree(DirectoryReference Directory)
		{
			lock(QueuedDirectories)
			{
				if(QueuedDirectories.Add(Directory))
				{
					Enqueue(() => RecursivelyScanDirectoryTree(DirectoryItem.GetItemByDirectoryReference(Directory)));
				}
			}
		}

		// Wait for the prefetcher to complete all reqeusted tasks
		public static void Wait()
		{
			ThreadWorkerQueue.Wait();
		}

		// Stop prefetching items, and cancel all pending tasks. synchronous.
		public static void Stop()
		{
			CancelSource.Cancel();
			ThreadWorkerQueue.Wait();
		}

		// Enqueue a task which checks for the cancellation token first
		// <param name="Action">Action to enqueue</param>
		private static void Enqueue(System.Action Action)
		{
			ThreadWorkerQueue.Enqueue(() => { if(!CancelToken.IsCancellationRequested){ Action(); } });
		}

		// Scans the engine directory, adding tasks for subdirectories
		private static void ScanEngineDirectory()
		{
			DirectoryItem EngineDirectory = DirectoryItem.GetItemByDirectoryReference(BuildTool.EngineDirectory);
			EngineDirectory.CacheDirectories();

			DirectoryItem EnginePluginsDirectory = DirectoryItem.Combine(EngineDirectory, Tag.Directory.Plugins);
			Enqueue(() => ScanPluginFolder(EnginePluginsDirectory));

			DirectoryItem EngineRuntimeDirectory = DirectoryItem.GetItemByDirectoryReference(BuildTool.EngineSourceRuntimeDirectory);
			Enqueue(() => RecursivelyScanDirectoryTree(EngineRuntimeDirectory));

			DirectoryItem EngineDeveloperDirectory = DirectoryItem.GetItemByDirectoryReference(BuildTool.EngineSourceDeveloperDirectory);
			Enqueue(() => RecursivelyScanDirectoryTree(EngineDeveloperDirectory));

			DirectoryItem EngineEditorDirectory = DirectoryItem.GetItemByDirectoryReference(BuildTool.EngineSourceEditorDirectory);
			Enqueue(() => RecursivelyScanDirectoryTree(EngineEditorDirectory));
		}

		// Scans a project directory, adding tasks for subdirectories
		// <param name="ProjectDirectory">The project directory to search</param>
		private static void ScanProjectDirectory(DirectoryItem ProjectDirectory)
		{
			DirectoryItem ProjectPluginsDirectory = DirectoryItem.Combine(ProjectDirectory, Tag.Directory.Plugins);
			Enqueue(() => ScanPluginFolder(ProjectPluginsDirectory));

			DirectoryItem ProjectSourceDirectory = DirectoryItem.Combine(ProjectDirectory, Tag.Directory.SourceCode);
			Enqueue(() => RecursivelyScanDirectoryTree(ProjectSourceDirectory));
		}

		// Scans a plugin parent directory, adding tasks for subdirectories
		// <param name="Directory">The directory which may contain plugin directories</param>
		private static void ScanPluginFolder(DirectoryItem Directory)
		{
			foreach(DirectoryItem SubDirectory in Directory.EnumerateSubDirectories())
			{
				if(SubDirectory.EnumerateAllCachedFiles().Any(x => x.HasExtension(Tag.Ext.Plugin)))
				{
					Enqueue(() => RecursivelyScanDirectoryTree(DirectoryItem.Combine(SubDirectory, Tag.Directory.SourceCode)));
				}
				else
				{
					Enqueue(() => ScanPluginFolder(SubDirectory));
				}
			}
		}
		
		// Scans an arbitrary directory tree
		static void RecursivelyScanDirectoryTree(DirectoryItem Directory)
		{
			foreach(DirectoryItem SubDirectory in Directory.EnumerateSubDirectories())
			{
				Enqueue(() => RecursivelyScanDirectoryTree(SubDirectory));
			}
			Directory.CacheFiles();
		}
	}
}
