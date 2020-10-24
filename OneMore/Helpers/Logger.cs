﻿//************************************************************************************************
// Copyright © 2016 Steven M. Cohn. All Rights Reserved.
//************************************************************************************************

namespace River.OneMoreAddIn
{
	using System;
	using System.Diagnostics;
	using System.IO;
	using System.Text;
	using System.Threading;


	/// <summary>
	/// Provide clean access to a simple output text file.
	/// </summary>

	internal class Logger : ILogger
	{
		private static ILogger instance;
		private static bool designMode;

		private readonly bool stdio;
		private readonly int processId;
		private string preamble;
		private bool isNewline;
		private bool isDisposed;
		private TextWriter writer;
		private Stopwatch clock;


		private Logger()
		{
			using (var process = Process.GetCurrentProcess())
			{
				processId = process.Id;
				stdio = process.ProcessName.StartsWith("LINQPad");
			}

			if (!stdio)
			{
				LogPath = Path.Combine(
					Path.GetTempPath(),
					designMode ? "OneMore-design.log" : "OneMore.log");
			}

			preamble = string.Empty;
			writer = null;
			isNewline = true;
			isDisposed = false;
		}


		#region Lifecycle
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}


		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (!isDisposed)
				{
					clock?.Stop();
					clock = null;

					if (writer != null)
					{
						writer.Flush();
						writer.Dispose();
						writer = null;
					}

					isDisposed = true;
				}
			}
		}
		#endregion Lifecycle


		public static ILogger Current
		{
			get
			{
				if (instance == null)
				{
					instance = new Logger();
				}

				return instance;
			}
		}


		public string LogPath { get; }


		private bool EnsureWriter()
		{
			if (stdio)
				return true;

			if (writer == null)
			{
				try
				{
					// allow the UTF8 output stream to handle Unicode characters
					// by falling back to default replacement characters like '?'
					var encodingWithFallback = (Encoding)(new UTF8Encoding(false)).Clone();
					encodingWithFallback.EncoderFallback = EncoderFallback.ReplacementFallback;
					encodingWithFallback.DecoderFallback = DecoderFallback.ReplacementFallback;

					writer = new StreamWriter(LogPath, true, encodingWithFallback);
				}
				catch
				{
					writer = null;
				}
			}

			return (writer != null);
		}


		public void Clear()
		{
			if (writer != null)
			{
				writer.Flush();
				writer.Dispose();
				writer = null;
			}

			File.Delete(LogPath);

			preamble = string.Empty;
			isNewline = true;

			if (EnsureWriter())
			{
				WriteLine("Log restarted");
			}
		}


		public void End()
		{
			preamble = string.Empty;
		}


		// For VS Forms designer
		public static void SetDesignMode(bool mode)
		{
			designMode = mode;
		}



		public void Start(string message)
		{
			WriteLine(message);
			preamble = "..";
		}


		public void StartClock()
		{
			if (clock == null)
			{
				clock = new Stopwatch();
			}

			if (clock.IsRunning)
			{
				clock.Restart();
			}
			else
			{
				clock.Start();
			}
		}


		public void StopClock()
		{
			clock?.Stop();
		}


		public void Write(string message)
		{
			if (EnsureWriter())
			{
				if (isNewline)
				{
					writer.Write(MakeHeader());
				}

				if (stdio)
					Console.Write(message);
				else
					writer.Write(message);

				isNewline = false;
			}
		}


		public void WriteLine()
		{
			if (EnsureWriter())
			{
				if (stdio)
				{
					Console.WriteLine();
				}
				else
				{
					writer.WriteLine();
				}
			}
		}


		public void WriteLine(string message)
		{
			if (EnsureWriter())
			{
				if (isNewline)
				{
					writer.Write(MakeHeader());
				}

				if (stdio)
				{
					Console.WriteLine(message);
				}
				else
				{
					writer.WriteLine(message);
					writer.Flush();
				}

				isNewline = true;
			}
		}


		public void WriteLine(Exception exc)
		{
			if (EnsureWriter())
			{
				if (isNewline)
				{
					writer.Write(MakeHeader());
				}

				if (stdio)
				{
					Console.WriteLine(Serialize(exc));
				}
				else
				{
					writer.WriteLine(Serialize(exc));
					writer.Flush();
				}

				isNewline = true;
			}
		}


		public void WriteLine(string message, Exception exc)
		{
			WriteLine(message);
			WriteLine(exc);
		}


		public void WriteTime(string message)
		{
			if (clock == null)
			{
				WriteLine($"{message} @ <no time to report>");
				return;
			}

			if (clock.IsRunning)
			{
				clock.Stop();
			}

			WriteLine(string.Format("{0} @ {1:00}.{2:00}s", 
				message, clock.Elapsed.Seconds, clock.Elapsed.Milliseconds / 10));
		}


		private string MakeHeader()
		{
			//$"{DateTime.Now.ToString("hh:mm:ss.fff")} [{Thread.CurrentThread.ManagedThreadId}] ";

			if (!stdio)
			{
				return $"{processId}:{Thread.CurrentThread.ManagedThreadId}] {preamble}";
			}

			return string.Empty;
		}


		private string Serialize(Exception exc)
		{
			var builder = new StringBuilder("EXCEPTION - ");
			builder.AppendLine(exc.GetType().FullName);

			Serialize(exc, builder);
			return builder.ToString();
		}


		private void Serialize(Exception exc, StringBuilder builder, int depth = 0)
		{
			if (depth > 0)
			{
				builder.AppendLine($"-- inner exception at depth {depth} ---------------");
			}

			builder.AppendLine("Message...: " + exc.Message);
			builder.AppendLine("StackTrace: " + exc.StackTrace);

			if (exc.TargetSite != null)
			{
				builder.AppendLine("TargetSite: [" +
					exc.TargetSite.DeclaringType.Assembly.GetName().Name + "] " +
					exc.TargetSite.DeclaringType + "::" +
					exc.TargetSite.Name + "()");
			}

			if (exc.InnerException != null)
			{
				Serialize(exc.InnerException, builder, depth + 1);
			}
		}
	}
}