﻿//************************************************************************************************
// Copyright © 2023 Steven M Cohn.  All rights reserved.
//************************************************************************************************

namespace River.OneMoreAddIn.Commands
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Xml.Linq;


	/// <summary>
	/// Provides thread-safe access to the navigation history file.
	/// This is used by both NavigationService and NavigatorWindow.
	/// </summary>
	internal class NavigationProvider : Loggable, IDisposable
	{
		private static readonly SemaphoreSlim semalock = new(1);
		private static readonly SemaphoreSlim semapub = new(1);

		private readonly string path;
		private FileSystemWatcher watcher;
		private EventHandler<List<HistoryRecord>> navigated;
		private DateTime lastWrite;
		private bool disposedValue;


		public NavigationProvider()
		{
			path = Path.Combine(PathHelper.GetAppDataPath(), "Navigator.xml");
			lastWrite = DateTime.MinValue;
		}


		#region Dispose
		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					if (watcher != null)
					{
						watcher.EnableRaisingEvents = false;
						watcher.Changed -= NavigationHandler;
						watcher.Dispose();
						watcher = null;
					}
				}

				disposedValue = true;
			}
		}

		public void Dispose()
		{
			// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
		#endregion Dispose


		/// <summary>
		/// Adds or removes an event handler to signal that the user has navigated to a page
		/// and stayed long enough to be recorded as "read"
		/// </summary>
		public event EventHandler<List<HistoryRecord>> Navigated
		{
			add
			{
				var dir = Path.GetDirectoryName(path);
				var nam = Path.GetFileNameWithoutExtension(path);
				var ext = Path.GetExtension(path);
				watcher = new FileSystemWatcher(dir, $"{nam}*{ext}")
				{
					NotifyFilter = NotifyFilters.LastWrite
				};
				watcher.Changed += NavigationHandler;
				watcher.EnableRaisingEvents = true;
				navigated += value;
			}

			remove
			{
				navigated -= value;
				watcher.EnableRaisingEvents = false;
				watcher.Changed -= NavigationHandler;
				watcher.Dispose();
				watcher = null;
			}
		}


		private async void NavigationHandler(object sender, FileSystemEventArgs e)
		{
			try
			{
				await semapub.WaitAsync();

				// time-check will prevent known bug/feature where FileSystemWatcher.Changed
				// seems to raise duplicate events within just a couple of ticks of each other;
				// throttle should be less than NavigationService.PollingInterval

				var time = File.GetLastWriteTime(e.FullPath);
				if (time.Subtract(lastWrite).TotalMilliseconds > NavigationService.SafeWatchWindow)
				{
					navigated?.Invoke(this, await ReadHistory());
					lastWrite = time;
				}
			}
			finally
			{
				semapub.Release();
			}
		}


		// - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

		/// <summary>
		/// Records the given list of page IDs as "pinned" items.
		/// </summary>
		/// <param name="list">A list of page IDs</param>
		/// <returns>True if the pinned list is updated; false if no changes needed</returns>
		public async Task<bool> PinPages(List<string> list)
		{
			await semalock.WaitAsync();

			try
			{
				var root = await Read();

				var pinned = root.Element("pinned");
				if (pinned == null)
				{
					pinned = new XElement("pinned");
					root.Add(pinned);
				}

				var updated = false;
				list.ForEach(id =>
				{
					if (!pinned.Elements().Any(e => e.Value == id))
					{
						pinned.Add(new XElement("page", id));
						updated = true;
					}
				});


				if (updated)
				{
					await Save(root);
				}

				return updated;
			}
			finally
			{
				semalock.Release();
			}
		}


		/// <summary>
		/// Returns the list of pinned items saved by the user.
		/// </summary>
		/// <returns></returns>
		public async Task<List<HistoryRecord>> ReadPinned()
		{
			await semalock.WaitAsync();

			try
			{
				var root = await Read();

				var pinned = root.Element("pinned");
				if (pinned != null)
				{
					return pinned.Elements()
						.Select(e => new HistoryRecord
						{
							PageID = e.Value
						})
						.ToList();
				}

				return new List<HistoryRecord>();
			}
			finally
			{
				semalock.Release();
			}
		}


		/// <summary>
		/// Returns the list of history items tracking visited pages.
		/// </summary>
		/// <returns></returns>
		public async Task<List<HistoryRecord>> ReadHistory()
		{
			try
			{
				await semalock.WaitAsync();

				var root = await Read();

				var history = root.Element("history");
				if (history == null)
				{
					return new List<HistoryRecord>();
				}

				return history.Elements("page")
					.Select(e => new HistoryRecord
					{
						PageID = e.Value,
						Visited = long.Parse(e.Attribute("visited").Value)
					})
					.ToList();
			}
			finally
			{
				semalock.Release();
			}
		}


		/// <summary>
		/// Records the given page ID as a visited page, marking it with the current time
		/// to record the "last visited" time.
		/// </summary>
		/// <param name="pageID">The ID of the visited page</param>
		/// <param name="depth">The maximum number of history records to maintain</param>
		/// <returns></returns>
		public async Task<bool> RecordHistory(string pageID, int depth)
		{
			if (string.IsNullOrWhiteSpace(pageID))
			{
				return false;
			}

			await semalock.WaitAsync();

			try
			{
				var root = await Read();

				var history = root.Element("history");
				if (history == null)
				{
					history = new XElement("history");
					root.Add(history);
				}

				var updated = false;

				var node = history.Elements("page").FirstOrDefault(e => e.Value == pageID);
				if (node == null)
				{
					node = new XElement("page", pageID);
					history.AddFirst(node);
					updated = true;
				}
				else
				{
					if (node != history.Elements().First())
					{
						node.Remove();
						history.AddFirst(node);
						updated = true;
					}
				}

				if (updated)
				{
					node.SetAttributeValue("visited", DateTime.Now.GetTickSeconds());

					if (history.Elements().Count() > depth)
					{
						history.Elements().Skip(depth).Remove();
					}

					await Save(root);
				}

				return updated;
			}
			finally
			{
				semalock.Release();
			}
		}


		/// <summary>
		/// Saves the pinned list; used when reordering pages
		/// </summary>
		/// <param name="list">Reordered list of page IDs</param>
		/// <returns></returns>
		public async Task SavePinned(List<string> list)
		{
			await semalock.WaitAsync();

			try
			{
				var root = await Read();

				var pinned = root.Element("pinned");
				if (pinned == null)
				{
					pinned = new XElement("pinned");
					root.Add(pinned);
				}

				// clear
				pinned.Elements().Remove();

				// add
				list.ForEach(id =>
				{
					pinned.Add(new XElement("page", id));
				});


				await Save(root);
			}
			finally
			{
				semalock.Release();
			}
		}


		/// <summary>
		/// Removes the list of page IDs from the pinned list.
		/// </summary>
		/// <param name="list"></param>
		/// <returns></returns>
		public async Task<bool> UnpinPages(List<string> list)
		{
			await semalock.WaitAsync();

			try
			{
				var root = await Read();

				var pinned = root.Element("pinned");
				if (pinned == null)
				{
					pinned = new XElement("pinned");
					root.Add(pinned);
				}

				var updated = false;
				list.ForEach(id =>
				{
					var pin = pinned.Elements().FirstOrDefault(e => e.Value == id);
					if (pin != null)
					{
						pin.Remove();
						updated = true;
					}
				});


				if (updated)
				{
					await Save(root);
				}

				return updated;
			}
			finally
			{
				semalock.Release();
			}
		}


		private async Task<XElement> Read()
		{
			XElement root = null;

			if (File.Exists(path))
			{
				try
				{
					// ensure we have ReadWrite sharing enabled so we don't block access
					// between NavigationService and NavigationDialog
					using var stream = new FileStream(path,
						FileMode.Open,
						FileAccess.Read,
						FileShare.ReadWrite);

					using var reader = new StreamReader(stream);

					root = XElement.Parse(await reader.ReadToEndAsync());
				}
				catch (Exception exc)
				{
					logger.WriteLine($"error reading {path}", exc);
					root = null;
				}
			}

			// file not found or problem reading then initialize with defaults
			root ??= new XElement("navigation",
				new XElement("history"),
				new XElement("pinned")
			);

			return root;
		}


		private async Task Save(XElement root)
		{
			var xml = root.ToString(SaveOptions.None);

			try
			{
				// ensure we have ReadWrite sharing enabled so we don't block access
				// between NavigationService and NavigationDialog
				using var stream = new FileStream(path,
					FileMode.OpenOrCreate,
					FileAccess.Write,
					FileShare.ReadWrite);

				// clear contents if writing less bytes than file contains
				stream.SetLength(0);

				using var writer = new StreamWriter(stream);
				await writer.WriteAsync(xml);
			}
			catch (Exception exc)
			{
				logger.WriteLine($"error saving {path}", exc);
			}
		}
	}
}
