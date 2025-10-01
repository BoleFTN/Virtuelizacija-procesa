using Common;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.ServiceModel;

namespace Server
{
	[ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Single)]
	public class MotorService : IMotorService, IDisposable
	{
		private readonly string storageRoot = ConfigurationManager.AppSettings["motorStoragePath"] ?? "MotorStorage";
		private FileStream measurementsStream;
		private StreamWriter measurementsWriter;
		private FileStream rejectsStream;
		private StreamWriter rejectsWriter;
		private FileStream analyticsStream;
		private StreamWriter analyticsWriter;

		private string currentSessionId;
		private double iqThreshold;
		private double idThreshold;
		private double tThreshold;
		private double deviationPct;
		
		// Previous values for delta calculations
		private double? lastIq;
		private double? lastId;
		private double? lastCoolant;
		
		// Running mean for coolant temperature
		private double runningCoolantMean;
		private long count;
		private int written;
		private readonly object lockObject = new object();

		public event EventHandler<string> OnTransferStarted;
		public event EventHandler<string> OnSampleReceived;
		public event EventHandler<string> OnTransferCompleted;
		public event EventHandler<string> OnWarningRaised;

		public MotorService()
		{
			OnTransferStarted += (s, m) => 
			{
				Console.WriteLine("\n--- Motor Session Started ---");
				Console.WriteLine(m);
				Console.WriteLine();
			};
			
			OnSampleReceived += (s, m) => 
			{
				if (written % 25 == 0) 
				{
					Console.Write($"\nProcessing: ");
				}
				Console.Write(".");
			};
			
			OnTransferCompleted += (s, m) => 
			{
				Console.WriteLine("\n\n--- Session Completed ---");
				Console.WriteLine(m);
				Console.WriteLine();
			};
			
			OnWarningRaised += (s, m) => 
			{
				Console.WriteLine($"\n*** ALERT: {m} ***\n");
			};
		}

		public Ack StartSession(StartSessionMeta meta)
		{
			lock (lockObject)
			{
				try
				{
					if (meta == null)
						return new Ack { Success = false, Message = "Meta is null", Status = "NACK" };

					// Validate thresholds
					if (meta.IqThreshold <= 0)
						return new Ack { Success = false, Message = "IqThreshold must be positive", Status = "NACK" };
					if (meta.IdThreshold <= 0)
						return new Ack { Success = false, Message = "IdThreshold must be positive", Status = "NACK" };
					if (meta.TThreshold <= 0)
						return new Ack { Success = false, Message = "TThreshold must be positive", Status = "NACK" };
					if (meta.DeviationPercent <= 0 || meta.DeviationPercent > 100)
						return new Ack { Success = false, Message = "DeviationPercent must be between 0 and 100", Status = "NACK" };

					currentSessionId = string.IsNullOrWhiteSpace(meta.SessionId) ? Guid.NewGuid().ToString("N") : meta.SessionId;
					iqThreshold = meta.IqThreshold;
					idThreshold = meta.IdThreshold;
					tThreshold = meta.TThreshold;
					deviationPct = meta.DeviationPercent;

					string sessionDir = Path.Combine(storageRoot, currentSessionId);
					Directory.CreateDirectory(sessionDir);
					measurementsStream = new FileStream(Path.Combine(sessionDir, "measurements_session.csv"), FileMode.Create, FileAccess.Write, FileShare.Read);
					measurementsWriter = new StreamWriter(measurementsStream) { AutoFlush = true };
					rejectsStream = new FileStream(Path.Combine(sessionDir, "rejects.csv"), FileMode.Create, FileAccess.Write, FileShare.Read);
					rejectsWriter = new StreamWriter(rejectsStream) { AutoFlush = true };
					analyticsStream = new FileStream(Path.Combine(sessionDir, "analytics_alerts.csv"), FileMode.Create, FileAccess.Write, FileShare.Read);
					analyticsWriter = new StreamWriter(analyticsStream) { AutoFlush = true };
					
					measurementsWriter.WriteLine("Timestamp,Iq,Id,Coolant,ProfileId,Ambient,Torque");
					rejectsWriter.WriteLine("Reason,Line");
					analyticsWriter.WriteLine("Timestamp,AlertType,Message,Value,Threshold");

					lastIq = null;
					lastId = null;
					lastCoolant = null;
					runningCoolantMean = 0;
					count = 0;
					written = 0;

					OnTransferStarted?.Invoke(this, $"Session ID: {currentSessionId}\nStarted: {meta.StartedAt:HH:mm:ss}\nStorage: {sessionDir}\nThresholds: Iq={iqThreshold}A, Id={idThreshold}A, T={tThreshold}°C, Deviation={deviationPct}%");
					return new Ack { Success = true, Message = "Session started", Status = "IN_PROGRESS" };
				}
				catch (Exception ex)
				{
					return new Ack { Success = false, Message = ex.Message, Status = "NACK" };
				}
			}
		}

		public Ack PushSample(MotorSample sample)
		{
			lock (lockObject)
			{
				try
				{
					if (measurementsWriter == null)
					{
						Console.WriteLine("\n❌ Motor Session not started - measurementsWriter is null");
						return new Ack { Success = false, Message = "Session not started", Status = "NACK" };
					}

					// Validate sample
					var valid = ValidateMotorSample(sample, out string valError);
					if (!valid)
					{
						Console.WriteLine($"\nREJECTED: {valError}");
						rejectsWriter?.WriteLine(string.Join(",", valError.Replace(',', ';'), SerializeMotorSample(sample)));
						return new Ack { Success = false, Message = valError, Status = "IN_PROGRESS" };
					}

					try
					{
						measurementsWriter.WriteLine(string.Join(",",
							sample.Timestamp.ToString("O"),
							sample.Iq.ToString(CultureInfo.InvariantCulture),
							sample.Id.ToString(CultureInfo.InvariantCulture),
							sample.Coolant.ToString(CultureInfo.InvariantCulture),
							sample.ProfileId.ToString(CultureInfo.InvariantCulture),
							sample.Ambient.ToString(CultureInfo.InvariantCulture),
							sample.Torque.ToString(CultureInfo.InvariantCulture)));
						written++;
						if (written <= 3)
						{
							Console.WriteLine($"\nSample #{written}: Iq={sample.Iq:F3}A, Id={sample.Id:F3}A, Coolant={sample.Coolant:F1}°C, Torque={sample.Torque:F2}Nm");
						}
					}
					catch (Exception ioex)
					{
						rejectsWriter?.WriteLine(string.Join(",", ("WriteError: " + ioex.Message).Replace(',', ';'), SerializeMotorSample(sample)));
						return new Ack { Success = false, Message = ioex.Message, Status = "IN_PROGRESS" };
					}

					// ===== ANALITIKA 1: Detekcija naglih promena strujnih komponenti (ΔIq, ΔId) =====
					if (lastIq.HasValue)
					{
						double deltaIq = sample.Iq - lastIq.Value;
						if (Math.Abs(deltaIq) > iqThreshold)
						{
							string direction = deltaIq > 0 ? "IZNAD očekivanog" : "ISPOD očekivanog";
							string message = $"ELECTRIC SPIKE Q: ΔIq={deltaIq:F3}A ({direction}) | Threshold: {iqThreshold:F3}A";
							string csvMessage = $"ELECTRIC SPIKE Q ΔIq={deltaIq:F3} A ({direction}) Threshold={iqThreshold:F3} A";
							OnWarningRaised?.Invoke(this, message);
							analyticsWriter?.WriteLine($"{sample.Timestamp:O},ElectricSpikeQ,{csvMessage},{deltaIq:F3},{iqThreshold:F3}");
						}
					}
					lastIq = sample.Iq;

					if (lastId.HasValue)
					{
						double deltaId = sample.Id - lastId.Value;
						if (Math.Abs(deltaId) > idThreshold)
						{
							string direction = deltaId > 0 ? "IZNAD očekivanog" : "ISPOD očekivanog";
							string message = $"ELECTRIC SPIKE D: ΔId={deltaId:F3}A ({direction}) | Threshold: {idThreshold:F3}A";
							string csvMessage = $"ELECTRIC SPIKE D ΔId={deltaId:F3} A ({direction}) Threshold={idThreshold:F3} A";
							OnWarningRaised?.Invoke(this, message);
							analyticsWriter?.WriteLine($"{sample.Timestamp:O},ElectricSpikeD,{csvMessage},{deltaId:F3},{idThreshold:F3}");
						}
					}
					lastId = sample.Id;

					// ===== ANALITIKA 2: Detekcija naglih promena rashladne tečnosti (ΔT) =====
					if (lastCoolant.HasValue)
					{
						double deltaT = sample.Coolant - lastCoolant.Value;
						if (Math.Abs(deltaT) > tThreshold)
						{
							string direction = deltaT > 0 ? "IZNAD očekivanog" : "ISPOD očekivanog";
							string message = $"TEMPERATURE SPIKE: ΔT={deltaT:F3}°C ({direction}) | Threshold: {tThreshold:F3}°C";
							string csvMessage = $"TEMPERATURE SPIKE ΔT={deltaT:F3} C ({direction}) Threshold={tThreshold:F3} C";
							OnWarningRaised?.Invoke(this, message);
							analyticsWriter?.WriteLine($"{sample.Timestamp:O},TemperatureSpike,{csvMessage},{deltaT:F3},{tThreshold:F3}");
						}
					}
					lastCoolant = sample.Coolant;

					// ===== Running mean i ±25% odstupanje za temperaturu rashladne tečnosti =====
					runningCoolantMean = ((runningCoolantMean * count) + sample.Coolant) / (count + 1);
					count++;
					double low = runningCoolantMean * (1 - deviationPct / 100.0);
					double high = runningCoolantMean * (1 + deviationPct / 100.0);
					if (sample.Coolant < low)
					{
						string message = $"OUT OF BAND: Coolant temp ISPOD očekivane vrednosti | T={sample.Coolant:F1}°C < {low:F1}°C (Mean: {runningCoolantMean:F1}°C)";
						string csvMessage = $"OUT OF BAND Coolant temp ISPOD očekivane vrednosti T={sample.Coolant:F1} C < {low:F1} C Mean={runningCoolantMean:F1} C";
						OnWarningRaised?.Invoke(this, message);
						analyticsWriter?.WriteLine($"{sample.Timestamp:O},OutOfBandWarning,{csvMessage},{sample.Coolant:F1},{low:F1}");
					}
					else if (sample.Coolant > high)
					{
						string message = $"OUT OF BAND: Coolant temp IZNAD očekivane vrednosti | T={sample.Coolant:F1}°C > {high:F1}°C (Mean: {runningCoolantMean:F1}°C)";
						string csvMessage = $"OUT OF BAND Coolant temp IZNAD očekivane vrednosti T={sample.Coolant:F1} C > {high:F1} C Mean={runningCoolantMean:F1} C";
						OnWarningRaised?.Invoke(this, message);
						analyticsWriter?.WriteLine($"{sample.Timestamp:O},OutOfBandWarning,{csvMessage},{sample.Coolant:F1},{high:F1}");
					}

					OnSampleReceived?.Invoke(this, "motor_sample");
					return new Ack { Success = true, Message = "OK", Status = "IN_PROGRESS" };
				}
				catch (Exception ex)
				{
					rejectsWriter?.WriteLine(string.Join(",", ex.Message.Replace(',', ';'), SerializeMotorSample(sample)));
					return new Ack { Success = false, Message = ex.Message, Status = "IN_PROGRESS" };
				}
			}
		}

		public Ack EndSession()
		{
			lock (lockObject)
			{
				if (measurementsWriter == null)
					return new Ack { Success = false, Message = "No active session", Status = "NACK" };

				Dispose();
				OnTransferCompleted?.Invoke(this, $"Total samples processed: {written}\nSession ID: {currentSessionId}\nCompleted at: {DateTime.Now:HH:mm:ss}");
				return new Ack { Success = true, Message = "Session completed", Status = "COMPLETED" };
			}
		}

		private static string SerializeMotorSample(MotorSample s)
		{
			if (s == null) return "<null>";
			return $"{s.Timestamp:O},{s.Iq},{s.Id},{s.Coolant},{s.ProfileId},{s.Ambient},{s.Torque}";
		}

		private static bool ValidateMotorSample(MotorSample s, out string error)
		{
			error = string.Empty;
			if (s == null) { error = "Sample is null"; return false; }
			
			// Validate Iq (q-axis current)
			if (double.IsNaN(s.Iq) || double.IsInfinity(s.Iq))
			{ error = $"Invalid Iq: {s.Iq}"; return false; }
			
			// Validate Id (d-axis current)
			if (double.IsNaN(s.Id) || double.IsInfinity(s.Id))
			{ error = $"Invalid Id: {s.Id}"; return false; }
			
			// Validate Coolant temperature (must be positive in Kelvin or Celsius)
			if (double.IsNaN(s.Coolant) || double.IsInfinity(s.Coolant) || s.Coolant < -273.15)
			{ error = $"Invalid Coolant temperature: {s.Coolant}"; return false; }
			
			// Validate ProfileId
			if (s.ProfileId < 0)
			{ error = $"Invalid ProfileId: {s.ProfileId}"; return false; }
			
			// Validate Ambient temperature
			if (double.IsNaN(s.Ambient) || double.IsInfinity(s.Ambient) || s.Ambient < -273.15)
			{ error = $"Invalid Ambient temperature: {s.Ambient}"; return false; }
			
			// Validate Torque
			if (double.IsNaN(s.Torque) || double.IsInfinity(s.Torque))
			{ error = $"Invalid Torque: {s.Torque}"; return false; }
			
			// Validate Timestamp
			if (s.Timestamp == default(DateTime))
			{ error = $"Invalid Timestamp: {s.Timestamp}"; return false; }
			
			// Validation passed silently to avoid console spam
			return true;
		}

		public void Dispose()
		{
			lock (lockObject)
			{
				try { measurementsWriter?.Flush(); measurementsWriter?.Dispose(); } catch { }
				try { measurementsStream?.Dispose(); } catch { }
				try { rejectsWriter?.Flush(); rejectsWriter?.Dispose(); } catch { }
				try { rejectsStream?.Dispose(); } catch { }
				try { analyticsWriter?.Flush(); analyticsWriter?.Dispose(); } catch { }
				try { analyticsStream?.Dispose(); } catch { }
				measurementsWriter = null; measurementsStream = null; rejectsWriter = null; rejectsStream = null;
				analyticsWriter = null; analyticsStream = null;
			}
		}
	}
}


