using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.Serialization;

namespace Common
{
	[DataContract]
	public class MotorSample
	{
		[DataMember]
		public DateTime Timestamp { get; set; }

		[DataMember]
		public double Iq { get; set; } // q-axis current component

		[DataMember]
		public double Id { get; set; } // d-axis current component

		[DataMember]
		public double Coolant { get; set; } // Coolant temperature

		[DataMember]
		public int ProfileId { get; set; } // Profile ID

		[DataMember]
		public double Ambient { get; set; } // Ambient temperature

		[DataMember]
		public double Torque { get; set; } // Motor torque

		public static bool TryParseCsv(string csvLine, out MotorSample sample, out string error)
		{
			sample = null;
			error = string.Empty;
			if (string.IsNullOrWhiteSpace(csvLine))
			{
				error = "Empty line";
				return false;
			}

			// Split tolerantno: ",", ";" ili tab, i ukloni navodnike
			string cleaned = csvLine.Replace("\"", "");
			string[] parts = cleaned.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None);

			var ci = CultureInfo.InvariantCulture;
			DateTime ts = DateTime.UtcNow;
			double iq = 0, id = 0, coolant = 0, ambient = 0, torque = 0;
			int profileId = 0;

			bool parsed = false;
			
			// Format measures_v2.csv: u_q,coolant,stator_winding,u_d,stator_tooth,motor_speed,i_d,i_q,pm,stator_yoke,ambient,torque,profile_id
			if (parts.Length >= 13)
			{
				// Mapiranje polja iz measures_v2.csv
				// parts[0] = u_q, parts[1] = coolant, parts[6] = i_d, parts[7] = i_q, parts[10] = ambient, parts[11] = torque, parts[12] = profile_id
				double tmpIq, tmpId, tmpCoolant, tmpAmbient, tmpTorque;
				int tmpProfileId;
				
				if (double.TryParse(parts[7], NumberStyles.Float, ci, out tmpIq) &&  // i_q
					double.TryParse(parts[6], NumberStyles.Float, ci, out tmpId) &&   // i_d
					double.TryParse(parts[1], NumberStyles.Float, ci, out tmpCoolant) && // coolant
					int.TryParse(parts[12], NumberStyles.Integer, ci, out tmpProfileId) && // profile_id
					double.TryParse(parts[10], NumberStyles.Float, ci, out tmpAmbient) && // ambient
					double.TryParse(parts[11], NumberStyles.Float, ci, out tmpTorque)) // torque
				{
					iq = tmpIq; id = tmpId; coolant = tmpCoolant; profileId = tmpProfileId; ambient = tmpAmbient; torque = tmpTorque;
					ts = DateTime.UtcNow; // Generiši timestamp
					parsed = true;
				}
			}
			// Fallback za stari format: Timestamp,Iq,Id,Coolant,ProfileId,Ambient,Torque
			else if (parts.Length >= 6)
			{
				DateTime tmpTs;
				if (DateTime.TryParse(parts[0], ci, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out tmpTs)) ts = tmpTs; else ts = DateTime.UtcNow;
				
				double tmpIq, tmpId, tmpCoolant, tmpAmbient, tmpTorque;
				int tmpProfileId;
				
				if (double.TryParse(parts[1], NumberStyles.Float, ci, out tmpIq) &&
					double.TryParse(parts[2], NumberStyles.Float, ci, out tmpId) &&
					double.TryParse(parts[3], NumberStyles.Float, ci, out tmpCoolant) &&
					int.TryParse(parts[4], NumberStyles.Integer, ci, out tmpProfileId) &&
					double.TryParse(parts[5], NumberStyles.Float, ci, out tmpAmbient))
				{
					iq = tmpIq; id = tmpId; coolant = tmpCoolant; profileId = tmpProfileId; ambient = tmpAmbient;
					
					// Torque (kolona 6, opciono)
					if (parts.Length >= 7 && double.TryParse(parts[6], NumberStyles.Float, ci, out tmpTorque))
					{
						torque = tmpTorque;
					}
					parsed = true;
				}
			}

			// Fallback: pokušaj da izvučeš numeričke tokene redom
			if (!parsed)
			{
				var matches = Regex.Matches(cleaned, @"-?\d+(?:\.\d+)?");
				if (matches.Count >= 6)
				{
					iq = double.Parse(matches[0].Value, ci);
					id = double.Parse(matches[1].Value, ci);
					coolant = double.Parse(matches[2].Value, ci);
					profileId = int.Parse(matches[3].Value, ci);
					ambient = double.Parse(matches[4].Value, ci);
					if (matches.Count >= 7)
						torque = double.Parse(matches[5].Value, ci);
					ts = DateTime.UtcNow;
					parsed = true;
				}
			}

			if (!parsed)
			{
				error = $"Unable to parse line - found {parts.Length} parts, expected at least 6";
				return false;
			}

			sample = new MotorSample
			{
				Timestamp = ts,
				Iq = iq,
				Id = id,
				Coolant = coolant,
				ProfileId = profileId,
				Ambient = ambient,
				Torque = torque
			};
			return true;
		}
	}
}

