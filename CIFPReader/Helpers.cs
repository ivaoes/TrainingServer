using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using static CIFPReader.ProcedureLine;

namespace CIFPReader;

public record CIFP(GridMORA[] MORAs, Airspace[] Airspaces, Dictionary<string, Aerodrome> Aerodromes, Dictionary<string, HashSet<Coordinate>> Fixes, Dictionary<string, HashSet<Navaid>> Navaids, Dictionary<string, HashSet<Airway>> Airways, Dictionary<string, HashSet<Procedure>> Procedures, Dictionary<string, HashSet<Runway>> Runways)
{
	private CIFP() : this(Array.Empty<GridMORA>(), Array.Empty<Airspace>(), new(), new(), new(), new(), new(), new()) { }

	public int Cycle => Aerodromes.Values.Max(a => a.Cycle);

	public static CIFP Load()
	{
		if (File.Exists("CIFP.zip"))
		{
			using (ZipArchive archive = ZipFile.OpenRead("CIFP.zip"))
			{
				var zae = archive.GetEntry("FAACIFP18");
				if (zae is null)
					throw new FileNotFoundException("ZIP archive doesn't contain FAACIFP18.");

				zae.ExtractToFile("FAACIFP18");
			}
			File.Delete("CIFP.zip");
		}
		if (File.Exists("FAACIFP18"))
		{
			CIFP retval = new(File.ReadAllLines("FAACIFP18"));

			if (Directory.Exists("cifp"))
				Directory.Delete("cifp", true);

			retval.Save();
			File.Delete("FAACIFP18");
			return retval;
		}
		else if (Directory.Exists("cifp"))
			return new(
				JsonSerializer.Deserialize<GridMORA[]>(File.ReadAllText("cifp/mora.json")) ?? throw new Exception(),
				Array.Empty<Airspace>(), //JsonSerializer.Deserialize<Airspace[]>(File.ReadAllText("cifp/airspace.json")) ?? throw new Exception(),
				new(JsonSerializer.Deserialize<Aerodrome[]>(File.ReadAllText("cifp/aerodrome.json"))?.Select(a => new KeyValuePair<string, Aerodrome>(a.Identifier, a)) ?? throw new Exception()),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Coordinate>>>(File.ReadAllText("cifp/fix.json")) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Navaid>>>(File.ReadAllText("cifp/navaid.json")) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Airway>>>(File.ReadAllText("cifp/airway.json")) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Procedure>>>(File.ReadAllText("cifp/procedure.json")) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Runway>>>(File.ReadAllText("cifp/runway.json")) ?? throw new Exception()
			);
		else
		{
			HttpClient cli = new();
			string pageListing = cli.GetStringAsync(@"https://aeronav.faa.gov/Upload_313-d/cifp/").Result;
			Regex cifpZip = new(@"CIFP_\d+\.zip");
			string currentCifp = cifpZip.Matches(pageListing).Last().Value;
			byte[] cifpDat = cli.GetByteArrayAsync(@"https://aeronav.faa.gov/Upload_313-d/cifp/" + currentCifp).Result;
			File.WriteAllBytes("CIFP.zip", cifpDat);
			return Load();
		}
	}

	private void Save()
	{
		JsonSerializerOptions opts = new() { WriteIndented = true };

		Directory.CreateDirectory("cifp");
		File.WriteAllText("cifp/mora.json", JsonSerializer.Serialize(MORAs, opts));
		//File.WriteAllText("cifp/airspace.json", JsonSerializer.Serialize(Airspaces, opts));
		File.WriteAllText("cifp/aerodrome.json", JsonSerializer.Serialize(Aerodromes.Values.ToArray(), opts));
		File.WriteAllText("cifp/fix.json", JsonSerializer.Serialize(Fixes, opts));
		File.WriteAllText("cifp/navaid.json", JsonSerializer.Serialize(Navaids, opts));
		File.WriteAllText("cifp/airway.json", JsonSerializer.Serialize(Airways, opts));
		File.WriteAllText("cifp/procedure.json", JsonSerializer.Serialize(Procedures, opts));
		File.WriteAllText("cifp/runway.json", JsonSerializer.Serialize(Runways, opts));
	}

	public CIFP(string[] cifpFileLines) : this()
	{
		List<GridMORA> moras = new();
		List<Airspace> airspaces = new();
		List<SIDLine> sidSteps = new();
		List<STARLine> starSteps = new();
		List<ApproachLine> iapSteps = new();
		List<AirwayFixLine> awLines = new();

		for (int lineIndex = 0; lineIndex < cifpFileLines.Length; ++lineIndex)
		{
			string line() => cifpFileLines[lineIndex];

			if (line().StartsWith("HDR"))
				// Header rows not important here.
				continue;

			RecordLine? rl = RecordLine.Parse(line());
			switch (rl)
			{
				case GridMORA mora:
					moras.Add(mora);
					break;

				case ControlledAirspace al:
					List<ControlledAirspace> segments = new() { al };

					while (ControlledAirspace.TryParse(cifpFileLines[++lineIndex], out ControlledAirspace? ca) && ca.Center == al.Center && ca.MultiCD == al.MultiCD)
						segments.Add(ca);

					--lineIndex;

					if (!segments.Any(ca => ca.Boundary.BoundaryVia is ControlledAirspace.BoundaryViaType.RhumbLine))
						airspaces.Add(new(segments.ToArray()));
					break;

				case RestrictiveAirspace _:
					continue;

				case Navaid nav:
					if (!Navaids.ContainsKey(nav.Identifier))
						Navaids.Add(nav.Identifier, new());
					Navaids[nav.Identifier].Add(nav);

					if (!Fixes.ContainsKey(nav.Identifier))
						Fixes.Add(nav.Identifier, new());
					Fixes[nav.Identifier].Add(nav.Position);
					break;

				case SIDLine sl:
						sidSteps.Add(sl);

					while (SIDLine.TryParse(cifpFileLines[++lineIndex], out SIDLine? s))
						sidSteps.Add(s);

					--lineIndex;
					break;

				case STARLine sl:
						starSteps.Add(sl);

					while (STARLine.TryParse(cifpFileLines[++lineIndex], out STARLine? s))
						starSteps.Add(s);

					--lineIndex;
					break;

				case ApproachLine al:
					iapSteps.Add(al);

					while (ApproachLine.TryParse(cifpFileLines[++lineIndex], out ApproachLine? s))
						iapSteps.Add(s);

					--lineIndex;
					break;

				case Waypoint wp:
					if (!Fixes.ContainsKey(wp.Identifier))
						Fixes.Add(wp.Identifier, new());
					Fixes[wp.Identifier].Add(wp.Position);
					break;

				case PathPoint pp:
					if (!Fixes.ContainsKey(pp.Runway))
						Fixes.Add(pp.Runway, new());
					if (!Fixes.ContainsKey(pp.Airport + "/" + pp.Runway))
						Fixes.Add(pp.Airport + "/" + pp.Runway, new());

					Fixes[pp.Runway].Add(pp.Position);
					Fixes[pp.Airport + "/" + pp.Runway].Add(pp.Position);
					break;

				case AirwayFixLine af:
					 awLines.Add(af);

					for (int seqNum = af.SequenceNumber;
						AirwayFixLine.TryParse(cifpFileLines[++lineIndex], out AirwayFixLine? f) && f.AirwayIdentifier == af.AirwayIdentifier && f.SequenceNumber > seqNum;
						seqNum = f.SequenceNumber)
						awLines.Add(f);

					--lineIndex;
					break;

				case Aerodrome a:
					if (a is Airport)
						Aerodromes.Add(a.Identifier, a);
					if (!Fixes.ContainsKey(a.Identifier))
						Fixes.Add(a.Identifier, new());
					Fixes[a.Identifier].Add(a.Location);
					break;

				case Runway r:
					if (!Fixes.ContainsKey("RW" + r.Identifier))
						Fixes.Add("RW" + r.Identifier, new());
					if (!Fixes.ContainsKey(r.Airport + "/" + r.Identifier))
						Fixes.Add(r.Airport + "/" + r.Identifier, new());
					Fixes["RW" + r.Identifier].Add(r.Endpoint);
					Fixes[r.Airport + "/" + r.Identifier].Add(r.Endpoint);

					if (!Runways.ContainsKey(r.Airport))
						Runways.Add(r.Airport, new());

					Runways[r.Airport].Add(r);
					break;

				case AirportMSA _:
					continue;

				case null:
					continue;

				default:
					throw new NotImplementedException();
			}
		}

		string procName = string.Empty, procAp = string.Empty;
		List<AirwayFixLine> awAccumulator = new();
		foreach (AirwayFixLine afl in awLines)
		{
			if (procName != afl.AirwayIdentifier
			 || (awAccumulator.Any() && awAccumulator.Last().SequenceNumber >= afl.SequenceNumber))
			{
				if (awAccumulator.Any())
				{
					Airway aw = new(procName, awAccumulator.ToArray(), Fixes);

					if (!Airways.ContainsKey(aw.Identifier))
						Airways.Add(aw.Identifier, new());

					Airways[aw.Identifier].Add(aw);
					awAccumulator.Clear();
				}

				procName = afl.AirwayIdentifier;
			}

			awAccumulator.Add(afl);
		}

		if (awAccumulator.Any())
		{
			Airway aw = new(procName, awAccumulator.ToArray(), Fixes);

			if (!Airways.ContainsKey(aw.Identifier))
				Airways.Add(aw.Identifier, new());

			Airways[aw.Identifier].Add(aw);
		}

		List<SIDLine> sidAccumulator = new();
		foreach (SIDLine sl in sidSteps)
		{
			if ((sl.Airport, sl.Name) != (procAp, procName))
			{
				if (sidAccumulator.Any())
				{
					SID sid = new(sidAccumulator.ToArray(), Fixes, Aerodromes);

					if (!Procedures.ContainsKey(sid.Name))
						Procedures.Add(sid.Name, new());
					Procedures[sid.Name].Add(sid);

					sidAccumulator.Clear();
				}

				procAp = sl.Airport;
				procName = sl.Name;
			}
			sidAccumulator.Add(sl);
		}

		if (sidAccumulator.Any())
		{
			SID sid = new(sidAccumulator.ToArray(), Fixes, Aerodromes);

			if (!Procedures.ContainsKey(sid.Name))
				Procedures.Add(sid.Name, new());
			Procedures[sid.Name].Add(sid);
		}

		List<STARLine> starAccumulator = new();
		foreach (STARLine sl in starSteps)
		{
			if ((sl.Airport, sl.Name) != (procAp, procName))
			{
				if (starAccumulator.Any())
				{
					STAR star = new(starAccumulator.ToArray(), Fixes, Aerodromes);

					if (!Procedures.ContainsKey(star.Name))
						Procedures.Add(star.Name, new());
					Procedures[star.Name].Add(star);

					starAccumulator.Clear();
				}

				procAp = sl.Airport;
				procName = sl.Name;
			}
			starAccumulator.Add(sl);
		}

		if (starAccumulator.Any())
		{
			STAR star = new(starAccumulator.ToArray(), Fixes, Aerodromes);

			if (!Procedures.ContainsKey(star.Name))
				Procedures.Add(star.Name, new());
			Procedures[star.Name].Add(star);
		}

		List<ApproachLine> iapAccumulator = new();
		foreach (ApproachLine al in iapSteps)
		{
			if ((al.Airport, al.Name) != (procAp, procName))
			{
				if (iapAccumulator.Any())
				{
					Approach iap = new(iapAccumulator.ToArray(), Fixes, Navaids, Aerodromes);

					if (!Procedures.ContainsKey(iap.Name))
						Procedures.Add(iap.Name, new());
					Procedures[iap.Name].Add(iap);

					iapAccumulator.Clear();
				}

				procAp = al.Airport;
				procName = al.Name;
			}
			iapAccumulator.Add(al);
		}

		if (iapAccumulator.Any())
		{
			Approach iap = new(iapAccumulator.ToArray(), Fixes, Navaids, Aerodromes);

			if (!Procedures.ContainsKey(iap.Name))
				Procedures.Add(iap.Name, new());
			Procedures[iap.Name].Add(iap);
		}

		(MORAs, Airspaces) = (moras.ToArray(), airspaces.ToArray());
	}
}

public record RecordLine(string Client, string Header, int FileRecordNumber, int Cycle)
{
	public RecordLine() : this("", "", 0, 0) { }

	public static RecordLine? Parse(string line) =>
		line[4] switch
		{
			'A' or 'U' => AirspaceLine.Parse(line),
			'D' => Navaid.Parse(line),
			'E' => EnrouteLine.Parse(line),
			'P' or 'H' => Aerodrome.Parse(line),

			_ => null
		};

	[DebuggerStepThrough]
	protected static void Fail(int charPos) =>
		throw new FormatException($"Invalid record format; failed on character {charPos}.");

	[DebuggerStepThrough]
	protected static void Check(string line, Index from, Index to, params string[] expected)
	{
		if (!expected.Contains(line[from..to]))
			Fail(from.Value);
	}

	[DebuggerStepThrough]
	protected static void CheckEmpty(string line, Index from, Index to) =>
		Check(line, from, to, new string(' ', to.Value - from.Value));
}

public record Radial(Navaid? Station, UnresolvedWaypoint? Waypoint, MagneticCourse Bearing) : IProcedureEndpoint, IProcedureVia
{
	private const decimal RADIAL_TRACKING_TOLERANCE = 0.5m; // Half a degree

	private decimal Magvar => Station?.MagneticVariation ?? throw new Exception("Cannot fly radials of DME.");

	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround)
	{
		if (Station is null)
			throw new Exception("Cannot fly a floating radial.");

		(TrueCourse? currentbearing, decimal distance) = Station.Position.GetBearingDistance(position);
		MagneticCourse currentRadial = currentbearing?.ToMagnetic(Magvar) ?? new(0, Magvar);
		decimal radialError = Bearing.Angle(currentRadial);

		if (distance < 0.1m)
			return IProcedureVia.TurnTowards(currentCourse, Bearing, refreshRate, onGround);

		if (radialError + RADIAL_TRACKING_TOLERANCE < 0)
			return IProcedureVia.TurnTowards(currentCourse, Bearing + 45, refreshRate, onGround);
		else if (radialError - RADIAL_TRACKING_TOLERANCE > 0)
			return IProcedureVia.TurnTowards(currentCourse, Bearing - 45, refreshRate, onGround);
		else
			return IProcedureVia.TurnTowards(currentCourse, Bearing - radialError, refreshRate, onGround);
	}

	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance)
	{
		if (Station is null)
			throw new Exception("Cannot reach a floating radial.");

		if (termination.HasFlag(PathTermination.UntilCrossing))
		{
			TrueCourse contextBearing = Station.Position.GetBearingDistance(context.position).bearing ?? throw new ArgumentException("Reference shouldn't be on top of endpoint.");

			if (context.reference is Coordinate refC)
			{
				TrueCourse? refBearing = Station.Position.GetBearingDistance(refC).bearing;
				if (refBearing is null)
					return true;
				else
					return (refBearing < Bearing.ToTrue()) ^ (contextBearing < Bearing.ToTrue());
			}
			else
				return Math.Abs(Bearing.Angle(contextBearing)) <= RADIAL_TRACKING_TOLERANCE;
		}
		else
			throw new NotImplementedException();
	}
}

[JsonConverter(typeof(RacetrackJsonConverter))]
public record Racetrack(Coordinate? Point, UnresolvedWaypoint? Waypoint, Course InboundCourse, decimal? Distance, TimeSpan? Time, bool LeftTurns = false) : IProcedureVia
{
	private const decimal FIX_CROSSING_MAX_ERROR = 0.1m;

	private HoldState? state = null;
	private EntryType? entry = null;
	private Coordinate? abeamPoint = null;
	private DateTime? abeamTime = null;
	private bool stable = true;

	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround)
	{
		if (Distance is null && Time is null)
			throw new Exception("Racetrack must have a distance or time defined.");
		if (Point is null)
			throw new Exception("Cannot fly a floating racetrack.");

		if (state is null)
		{
			state = HoldState.Entry;
			entry = (LeftTurns, -currentCourse.Angle(InboundCourse)) switch
			{
				(false,	>= -70 and <= 110) or
				(true,	<= 70 and >= -110) => EntryType.Direct,
				(false,	< -70) or
				(true,	> 70)  => EntryType.Parallel,
				(false,	> 110) or
				(true,	< -110) => EntryType.Teardrop
			};

			stable = true;
		}

		(TrueCourse? fixBearing, decimal distance) = position.GetBearingDistance(Point.Value);
		switch (state.Value)
		{
			case HoldState.Entry:
				if (distance < FIX_CROSSING_MAX_ERROR)
				{
					state = HoldState.Outbound;
					stable = false;
				}

				return IProcedureVia.TurnTowards(currentCourse, fixBearing ?? currentCourse, refreshRate, onGround, stable ? null : LeftTurns);

			case HoldState.Inbound:
				if (!stable && Math.Abs(currentCourse.Angle(InboundCourse)) < 1)
					stable = true;

				if (distance < FIX_CROSSING_MAX_ERROR)
				{
					state = HoldState.Outbound;
					abeamPoint = null;
					abeamTime = null;
					entry = null;
					stable = false;
				}

				return IProcedureVia.TurnTowards(currentCourse, fixBearing ?? InboundCourse, refreshRate, onGround, stable ? null : LeftTurns);

			case HoldState.Outbound:
				switch (entry)
				{
					case EntryType.Direct:
						entry = null;
						goto case null;

					case EntryType.Parallel:
						abeamTime ??= DateTime.UtcNow;

						if ((DateTime.UtcNow - abeamTime.Value).TotalMinutes < 1)
						{
							stable = true;
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal, refreshRate, onGround);
						}

						abeamTime = null;
						stable = false;
						state = HoldState.Inbound;
						return GetTrueCourse(position, currentCourse, refreshRate, onGround);

					case EntryType.Teardrop:
						abeamTime ??= DateTime.UtcNow;

						if ((DateTime.UtcNow - abeamTime.Value).TotalMinutes < 1)
						{
							stable = true;
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal + (LeftTurns ? 30 : -30), refreshRate, onGround);
						}

						abeamTime = null;
						stable = false;
						state = HoldState.Inbound;
						return GetTrueCourse(position, currentCourse, refreshRate, onGround);

					case null:
						if (!stable && Math.Abs(currentCourse.Angle(InboundCourse.Reciprocal)) < 1)
						{
							abeamPoint = position;
							abeamTime = DateTime.UtcNow;
							stable = true;
						}

						if (!stable)
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal, refreshRate, onGround, LeftTurns);

						if ((Distance	is not null && abeamPoint!.Value.DistanceTo(position)	>= Distance)
						 || (Time		is not null && DateTime.UtcNow - abeamTime!.Value		>= Time))
						{
							abeamPoint = null;
							abeamTime = null;
							stable = false;
							state = HoldState.Inbound;
							return GetTrueCourse(position, currentCourse, refreshRate, onGround);
						}
						else
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal, refreshRate, onGround);

					default:
						throw new Exception("Unreachable");
				}

			default:
				throw new Exception("Unreachable");
		}
	}

	private enum HoldState
	{
		Entry,
		Inbound,
		Outbound
	}

	private enum EntryType
	{
		Direct,
		Parallel,
		Teardrop
	}

	public class RacetrackJsonConverter : JsonConverter<Racetrack>
	{
		public override Racetrack? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			Coordinate? point = null;
			Course? inboundCourse = null;
			decimal? distance = null;
			TimeSpan? time = null;
			bool left = false;

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				string prop = reader.GetString() ?? throw new JsonException();
				reader.Read();
				switch (prop)
				{
					case "Point":
						point = JsonSerializer.Deserialize<Coordinate>(ref reader, options);
						break;

					case "InboundCourse":
						inboundCourse = JsonSerializer.Deserialize<Course>(ref reader, options);
						break;

					case "Leg" when reader.TokenType == JsonTokenType.Number:
						distance = reader.GetDecimal();
						break;

					case "Leg":
						time = JsonSerializer.Deserialize<TimeSpan>(ref reader, options);
						break;

					case "LeftTurns":
						left = reader.GetBoolean();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			if (point is null || inboundCourse is null || (distance is null && time is null))
				throw new JsonException();
			return new(point, null, inboundCourse, distance, time, left);
		}

		public override void Write(Utf8JsonWriter writer, Racetrack value, JsonSerializerOptions options)
		{
			if (value.Point is null)
				throw new ArgumentNullException(nameof(value));

			writer.WriteStartObject();
			writer.WritePropertyName("Point"); JsonSerializer.Serialize(writer, value.Point, options);
			writer.WritePropertyName("InboundCourse"); JsonSerializer.Serialize(writer, value.InboundCourse, options);
			writer.WritePropertyName("Leg");
			if (value.Distance is decimal d)
				writer.WriteNumberValue(d);
			else if (value.Time is TimeSpan t)
				JsonSerializer.Serialize(writer, t, options);
			else
				throw new ArgumentException("Racetrack must have leg distance or time.", nameof(value));

			if (value.LeftTurns)
				writer.WriteBoolean("LeftTurns", true);
			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(ArcJsonConverter))]
public record Arc(Coordinate? Centerpoint, UnresolvedWaypoint? Centerwaypoint, decimal Radius, MagneticCourse ArcTo) : IProcedureVia
{
	private const decimal ARC_RADIUS_TOLERANCE = 0.1m;

	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround)
	{
		if (Centerpoint is null)
			throw new Exception("Cannot fly a floating arc.");
		if (Radius <= 0)
			throw new Exception("Cannot fly an arc with 0 radius.");

		(TrueCourse? bearing, decimal distance) = Centerpoint.Value.GetBearingDistance(position);


		if (bearing is null || distance + ARC_RADIUS_TOLERANCE < Radius)
			return IProcedureVia.TurnTowards(currentCourse, bearing ?? ArcTo.ToTrue(), refreshRate, onGround);
		else if (distance - ARC_RADIUS_TOLERANCE > Radius)
			return IProcedureVia.TurnTowards(currentCourse, bearing.Reciprocal, refreshRate, onGround);

		Course targetBearing =
			bearing.Angle(ArcTo) > 0
			? bearing + 90	// Clockwise
			: bearing - 90;	// Anticlockwise

		return IProcedureVia.TurnTowards(currentCourse, targetBearing, refreshRate, onGround);
	}

	public class ArcJsonConverter : JsonConverter<Arc>
	{
		public override Arc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			Coordinate? centerpoint = null;
			MagneticCourse? arcTo = null;
			decimal? radius = null;

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				string prop = reader.GetString() ?? throw new JsonException();
				reader.Read();
				switch (prop)
				{
					case "Centerpoint":
						centerpoint = JsonSerializer.Deserialize<Coordinate>(ref reader, options);
						break;

					case "Radius":
						radius = reader.GetDecimal();
						break;

					case "ArcTo":
						arcTo = JsonSerializer.Deserialize<Course>(ref reader, options)?.ToMagnetic(null);
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			if (centerpoint is null || radius is null || arcTo is null || arcTo.Variation is null)
				throw new JsonException();

			return new(centerpoint, null, radius!.Value, arcTo!);
		}

		public override void Write(Utf8JsonWriter writer, Arc value, JsonSerializerOptions options)
		{
			if (value.Centerpoint is null || value.ArcTo.Variation is null)
				throw new ArgumentNullException(nameof(value));

			writer.WriteStartObject();
			writer.WritePropertyName("Centerpoint"); JsonSerializer.Serialize(writer, value.Centerpoint, options);
			writer.WritePropertyName("Radius"); writer.WriteNumberValue(value.Radius);
			writer.WritePropertyName("ArcTo"); JsonSerializer.Serialize<Course>(writer, value.ArcTo, options);
			writer.WriteEndObject();
		}
	}
}

public record AltitudeRestriction(Altitude? Minimum, Altitude? Maximum)
{
	public static AltitudeRestriction Unrestricted => new(null, null);
	public bool IsUnrestricted => Minimum is null && Maximum is null;

	public static AltitudeRestriction FromDescription(AltitudeDescription description, Altitude? alt1, Altitude? alt2)
	{
		if ((alt1 ?? alt2) is null)
			return Unrestricted;
		else if (alt1 is null)
			throw new ArgumentNullException(nameof(alt1));

		if ((char)description == 'I')
			// Intercept altitude given to be nice; ignore it.
			alt2 = null;
		else if ((char)description == 'G')
			// Restriction is above glideslope, so it's called out here as a warning.
			// Most of the time they're actually the same, just the ILS08 into KBUR is higher.
			alt2 = null;
		else if ("JHV".Contains((char)description) && alt2 is null)
			// KCOS & KILM have typoes in the procedures where a J or H is used instead of a +.
			description = AltitudeDescription.AtOrAbove;

		description = (char)description switch
		{
			' ' => AltitudeDescription.At,
			'J' or 'H' or 'V' => AltitudeDescription.Between,
			'I' or 'G' => AltitudeDescription.At,

			_ => description
		};

		if (description == AltitudeDescription.AtOrAbove && alt2 is not null)
		{
			// A couple of strange procedures here. Likely irregularities, though this includes one into KDTW.

			(description, alt1, alt2) = (alt1, alt2) switch
			{
				(Altitude a, Altitude b) when a == b => (AltitudeDescription.AtOrAbove, a, null),
				(Altitude a, Altitude b) when a < b => (AltitudeDescription.Between, a, b),
				(Altitude a, Altitude b) when a > b => (AltitudeDescription.AtOrAbove, a, null),
				_ => throw new Exception("Unreachable")
			};
		}
		else if (description == AltitudeDescription.AtOrBelow && alt2 is not null && alt2 > alt1)
			// Curse you KMEM!
			description = AltitudeDescription.Between;

		if (description == AltitudeDescription.Between && alt2 is null)
			throw new ArgumentNullException(nameof(alt2), "Between altitude restrictions need two altitudes.");
		else if (description != AltitudeDescription.Between && alt2 is not null)
			throw new ArgumentOutOfRangeException(nameof(alt2), "Single altitude restrictions should not be passed two altitudes.");

		return description switch
		{
			AltitudeDescription.Between => new(alt1, alt2),
			AltitudeDescription.At => new(alt1, alt1),

			AltitudeDescription.AtOrAbove => new(alt1, null),
			AltitudeDescription.AtOrBelow => new(null, alt1),

			_ => throw new ArgumentOutOfRangeException(nameof(description), "Provided altitude description is unknown.")
		};
	}

	public bool IsInRange(Altitude altitude) =>
		(Minimum ?? Altitude.MinValue) <= altitude && (Maximum ?? Altitude.MaxValue) >= altitude;

	public enum AltitudeDescription
	{
		AtOrAbove = '+',
		AtOrBelow = '-',
		At = '@',
		Between = 'B'
	}

	public override string ToString()
	{
		string retval = "";
		if (Minimum is not null)
			retval += @$"\{Minimum.Feet / 100} ";
		if (Maximum is not null)
			retval += $@"{Maximum.Feet / 100}\";
		retval = retval.Trim();

		return string.IsNullOrWhiteSpace(retval) ? "Unrestricted" : retval;
	}

	public static AltitudeRestriction Parse(string data)
	{
		if (data == "Unrestricted")
			return Unrestricted;

		int? min = null, max = null;
		if (data.StartsWith('\\'))
			min = int.Parse(data.Split()[0][1..]) * 100;
		if (data.EndsWith('\\'))
			max = int.Parse(data.Split().Last()[..^1]) * 100;
		
		return new(min is null ? null : new AltitudeMSL(min.Value), max is null ? null : new AltitudeMSL(max.Value));
	}
}
public record SpeedRestriction(uint? Minimum, uint? Maximum)
{
	public static SpeedRestriction Unrestricted => new(null, null);
	public bool IsUnrestricted => Minimum is null && Maximum is null;

	public bool IsInRange(uint speed) =>
		(Minimum ?? uint.MinValue) <= speed && (Maximum ?? uint.MaxValue) >= speed;

	public override string ToString()
	{
		string retval = "";
		if (Minimum is not null)
			retval += @$"\{Minimum}K ";
		if (Maximum is not null)
			retval += $@"{Maximum}K\";
		retval = retval.Trim();

		return string.IsNullOrWhiteSpace(retval) ? "Unrestricted" : retval;
	}
	
	public static SpeedRestriction Parse(string data)
	{
		if (data == "Unrestricted")
			return Unrestricted;

		uint? min = null, max = null;
		if (data.StartsWith('\\'))
			min = uint.Parse(data.Split()[0][1..^1]);
		if (data.EndsWith('\\'))
			max = uint.Parse(data.Split().Last()[..^2]);

		return new(min, max);
	}
}

public class UnresolvedWaypoint : IProcedureEndpoint
{
	internal string? Name { get; init; }
	protected Coordinate? Position { get; init; }

	public UnresolvedWaypoint(string name) => Name = name;
	public UnresolvedWaypoint(Coordinate coord) => Position = coord;

	public Coordinate Resolve(Dictionary<string, HashSet<Coordinate>> fixes, Coordinate? reference = null) =>
		Position ?? fixes.Concretize(Name!, refCoord: reference);
	public Coordinate Resolve(Dictionary<string, HashSet<Coordinate>> fixes, UnresolvedWaypoint? reference = null) =>
		Position ?? fixes.Concretize(Name!, refString: reference?.Name);

	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance) =>
		throw new Exception("Waypoint must be resolved.");
}

public static class Extensions
{
	public static Coordinate Concretize(this Dictionary<string, HashSet<Coordinate>> fixes, string wp, Coordinate? refCoord = null, string? refString = null)
	{
		if (!fixes.ContainsKey(wp))
			throw new ArgumentException($"Unknown waypoint {wp}.", nameof(wp));

		if (fixes[wp].Count == 1)
			return fixes[wp].Single();

		if (refCoord is not null)
			return fixes[wp].MinBy(wp => wp.DistanceTo(refCoord.Value));
		else if (refString is not null)
		{
			if (!fixes.ContainsKey(refString))
				throw new ArgumentException($"Unknown waypoint {refString}.", nameof(refString));

			return fixes[wp].MinBy(wp => fixes[refString].Min(rwp => wp.DistanceTo(rwp)));
		}
		else
			throw new Exception($"Could not resolve waypoint {wp} without context.");
	}

	public static (Coordinate Reference, decimal Variation) GetLocalMagneticVariation(this Dictionary<string, HashSet<Navaid>> navaids, Coordinate refCoord)
	{
		for (int maxDistance = 50; maxDistance < 250; maxDistance += 50)
			foreach (Navaid n in navaids.Values.SelectMany(ns => ns.Where(na => refCoord.DistanceTo(na.Position) < maxDistance)))
				if (n.MagneticVariation is not null)
					return (n.Position, n.MagneticVariation.Value);

		throw new Exception("Magnetic variation not found. Where the hell are you?");
	}

	public static (Coordinate Reference, decimal Variation) GetLocalMagneticVariation(this Dictionary<string, Aerodrome> aerodromes, Coordinate refCoord)
	{
		for (int maxDistance = 50; maxDistance < 250; maxDistance += 50)
			foreach (Aerodrome ad in aerodromes.Values.Where(a => refCoord.DistanceTo(a.Location) < maxDistance))
				switch (ad)
				{
					case Airport ap:
						return (ap.Location, ap.MagneticVariation);

					case Heliport hp:
						return (hp.Location, hp.MagneticVariation);

					default:
						continue;
				}

		throw new Exception("Magnetic variation not found. Where the hell are you?");
	}
}