using Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using System.Configuration;

namespace Client
{
    public class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== PMSM Motor Monitoring Client ===");
            Console.WriteLine("Pokretanje PMSM Motor Monitoring aplikacije...");
            Console.WriteLine("=====================================");
            
            RunMotorClient();
        }


        static void RunMotorClient()
        {
            ChannelFactory<IMotorService> motorFactory = null;
            IMotorService motorProxy = null;
            
            try
            {
                motorFactory = new ChannelFactory<IMotorService>("Motor");
                motorProxy = motorFactory.CreateChannel();
                
                // Test connection
                Console.WriteLine("Testing connection to PMSM Motor service...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect to Motor service: {ex.Message}");
                Console.WriteLine("Make sure the Server is running on localhost:4101");
                Console.ReadKey();
                return;
            }

            // Motor demo
            Console.WriteLine("Unesi putanju do PMSM Motor CSV fajla (Enter = auto-search):");
            string path = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(path))
            {
                // Search for motor dataset files
                string binDataset = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dataset", "measures_v2.csv");
                string projDataset = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Client", "Dataset", "measures_v2.csv");
                path = File.Exists(binDataset) ? binDataset : projDataset;

                if (!File.Exists(path))
                {
                    // Try to find any CSV file that might be motor data
                    string binDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dataset");
                    string projDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Client", "Dataset");
                    string found = null;
                    
                    if (Directory.Exists(binDir))
                    {
                        var files = Directory.GetFiles(binDir, "*.csv")
                            .Where(f => !Path.GetFileName(f).StartsWith("rejects_"))
                            .ToArray();
                        if (files.Length > 0) found = files[0];
                    }
                    if (found == null && Directory.Exists(projDir))
                    {
                        var files = Directory.GetFiles(projDir, "*.csv")
                            .Where(f => !Path.GetFileName(f).StartsWith("rejects_"))
                            .ToArray();
                        if (files.Length > 0) found = files[0];
                    }
                    if (found != null)
                    {
                        path = found;
                    }
                }
            }

            // Ako i dalje ne postoji, traži korisnika u petlji
            while (!File.Exists(path))
            {
                Console.WriteLine("Motor CSV nije pronađen. Unesi punu putanju do .csv fajla:");
                path = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(path))
                {
                    Console.WriteLine("Putanja prazna. Pokušaj ponovo.");
                    continue;
                }
            }

            var meta = new StartSessionMeta
            {
                SessionId = Guid.NewGuid().ToString("N"),
                StartedAt = DateTime.UtcNow,
                IqThreshold = double.Parse(ConfigurationManager.AppSettings["Iq_threshold"] ?? "1.0", CultureInfo.InvariantCulture),
                IdThreshold = double.Parse(ConfigurationManager.AppSettings["Id_threshold"] ?? "1.0", CultureInfo.InvariantCulture),
                TThreshold = double.Parse(ConfigurationManager.AppSettings["T_threshold"] ?? "5.0", CultureInfo.InvariantCulture),
                DeviationPercent = double.Parse(ConfigurationManager.AppSettings["DeviationPercent"] ?? "25", CultureInfo.InvariantCulture)
            };

            try
            {
                var ack = motorProxy.StartSession(meta);
                Console.WriteLine($"Motor session: {ack.Status}");
                if (!ack.Success)
                {
                    Console.WriteLine($"Error: {ack.Message}");
                    return;
                }

                int sent = 0;
                int successful = 0;
                int failed = 0;
                Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dataset"));
                string rejects = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dataset", $"rejects_motor_{meta.SessionId}.csv");
                
                Console.WriteLine($"Reading motor data from: {path}");
                Console.WriteLine($"File exists: {File.Exists(path)}");
                Console.WriteLine("Sending motor samples...");
                Console.WriteLine($"Thresholds: Iq_threshold={meta.IqThreshold}, Id_threshold={meta.IdThreshold}, T_threshold={meta.TThreshold}, Deviation={meta.DeviationPercent}%");
                Console.WriteLine("Watch for MOTOR ALERTS in Server console! 🚨");
                
                using (var reader = new MotorCsvReader(path, rejects))
                {
                    int loadedCount = 0;
                    while (loadedCount < 100 && reader.TryReadNext(out var sample))
                    {
                        loadedCount++;
                        var resp = motorProxy.PushSample(sample);
                        sent++;
                        if (resp.Success)
                            successful++;
                        else
                        {
                            failed++;
                            if (failed <= 3)
                            {
                                Console.WriteLine($"\n❌ Motor Client received failure: {resp.Message}");
                            }
                        }
                        
                        if (loadedCount % 10 == 0)
                            Console.Write($"\rLoaded: {loadedCount}, Sent: {sent}, Success: {successful}, Failed: {failed}");
                    }
                    Console.WriteLine($"\nMotor Loaded={loadedCount}, Accepted={reader.AcceptedCount}, Rejected={reader.RejectedCount}");
                }
                var end = motorProxy.EndSession();
                Console.WriteLine($"\nMotor session finished: {end.Status}");
                Console.WriteLine($"Total sent: {sent}, Successful: {successful}, Failed: {failed}");
                if (sent == 0)
                {
                    Console.WriteLine("Nije poslat nijedan motor uzorak. Proveri format CSV-a ili putanju.");
                }
            }
            catch (FaultException<CustomException> ex)
            {
                Console.WriteLine($"Motor ERROR: {ex.Detail.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Neočekivana greška: {ex.Message}");
            }

            finally
            {
                try
                {
                    if (motorProxy is ICommunicationObject commObj)
                        commObj.Close();
                    motorFactory?.Close();
                }
                catch { }
            }

            Console.WriteLine("\nPritisni bilo koji taster za izlaz...");
            Console.ReadKey();
        }
    }
}


