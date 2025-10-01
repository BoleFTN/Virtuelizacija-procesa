using Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace Server
{
    public class Program
    {
        static void Main(string[] args)
        {
            ServiceHost motorHost = null;
            
            try
            {
                // Start Motor Service  
                motorHost = new ServiceHost(typeof(MotorService));
                motorHost.Open();

                Console.WriteLine();
                Console.WriteLine("PMSM Motor Monitoring Server");
                Console.WriteLine("Motor Service Status: RUNNING");
                Console.WriteLine($"Endpoint: {motorHost.BaseAddresses[0]}/Motor");
                Console.WriteLine($"Protocol: NetTCP with Streaming");
                Console.WriteLine($"Started: {DateTime.Now:HH:mm:ss dd.MM.yyyy}");
                Console.WriteLine();
                Console.WriteLine("Waiting for client connections...");
                Console.WriteLine("Real-time motor analytics will appear below");
                Console.WriteLine();
                Console.WriteLine("Press any key to stop the service");
                
                Console.ReadKey();

                motorHost?.Close();

                Console.WriteLine("\nMotor service is STOPPED");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting service: {ex.Message}");
                Console.WriteLine("Make sure port is not in use by other applications");
            }
            finally
            {
                try
                {
                    motorHost?.Close();
                }
                catch { }
            }
            
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}


