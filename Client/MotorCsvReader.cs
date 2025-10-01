using Common;
using System;
using System.Globalization;
using System.IO;

namespace Client
{
	public class MotorCsvReader : IDisposable
	{
		private readonly FileStream fileStream;
		private readonly StreamReader reader;
		private readonly StreamWriter rejectWriter;
		private bool headerChecked = false;
		public int AcceptedCount { get; private set; }
		public int RejectedCount { get; private set; }

		public MotorCsvReader(string csvPath, string rejectLogPath)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(rejectLogPath));
			fileStream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			reader = new StreamReader(fileStream);
			rejectWriter = new StreamWriter(new FileStream(rejectLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
			rejectWriter.WriteLine("Reason,Line");
		}

		public bool TryReadNext(out MotorSample sample)
		{
			sample = null;
			while (true)
			{
				string line = reader.ReadLine();
				if (line == null) return false;

				// Prvi red može biti header – proveri samo jednom
				if (!headerChecked)
				{
					headerChecked = true;
					var low = line.ToLowerInvariant();
					if (low.Contains("timestamp") || low.Contains("iq") || low.Contains("id") || low.Contains("coolant") || low.Contains("ambient") || low.Contains("torque") || low.Contains("u_q") || low.Contains("stator_winding"))
					{
						continue; // preskoči header
					}
				}

				if (MotorSample.TryParseCsv(line, out sample, out var error))
				{
					AcceptedCount++;
					// Debug info za prve nekoliko uspešnih
					if (AcceptedCount <= 3)
					{
						Console.WriteLine($"\n✅ Motor Accepted line {AcceptedCount}: Iq={sample.Iq:F3}A, Id={sample.Id:F3}A, Coolant={sample.Coolant:F1}°C, Torque={sample.Torque:F2}Nm");
					}
					return true;
				}

				// loguj i nastavi čitanje
				rejectWriter.WriteLine(string.Join(",", error.Replace(',', ';'), line.Replace(',', ';')));
				rejectWriter.Flush();
				RejectedCount++;
				
				// Debug info za prve nekoliko grešaka
				if (RejectedCount <= 5)
				{
					Console.WriteLine($"\n❌ Motor Rejected line {RejectedCount}: {error}");
					Console.WriteLine($"Line preview: {line.Substring(0, Math.Min(100, line.Length))}...");
					Console.WriteLine($"Parts count: {line.Split(',').Length}");
				}
			}
		}

		public void Dispose()
		{
			try { reader?.Dispose(); } catch { }
			try { fileStream?.Dispose(); } catch { }
			try { rejectWriter?.Flush(); rejectWriter?.Dispose(); } catch { }
		}
	}
}


