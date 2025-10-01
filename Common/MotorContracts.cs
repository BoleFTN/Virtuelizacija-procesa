using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.ServiceModel;

namespace Common
{
	[DataContract]
	public class StartSessionMeta
	{
		[DataMember]
		public string SessionId { get; set; }

		[DataMember]
		public DateTime StartedAt { get; set; }

		[DataMember]
		public double DeviationPercent { get; set; }

		// PMSM Motor specific thresholds
		[DataMember]
		public double IqThreshold { get; set; }

		[DataMember]
		public double IdThreshold { get; set; }

		[DataMember]
		public double TThreshold { get; set; }
	}

	[DataContract]
	public class Ack
	{
		[DataMember]
		public bool Success { get; set; }

		[DataMember]
		public string Message { get; set; }

		[DataMember]
		public string Status { get; set; }
	}

	[ServiceContract]
	public interface IMotorService
	{
		[OperationContract]
		[FaultContract(typeof(CustomException))]
		Ack StartSession(StartSessionMeta meta);

		[OperationContract]
		[FaultContract(typeof(CustomException))]
		Ack PushSample(MotorSample sample);

		[OperationContract]
		[FaultContract(typeof(CustomException))]
		Ack EndSession();
	}
}


