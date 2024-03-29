﻿using System.Text.Json;
using System.Text.Json.Serialization;

using static CIFPReader.ProcedureLine;

namespace CIFPReader;

[JsonConverter(typeof(ProcedureJsonConverter))]
public class Procedure
{
	public string Name { get; init; }
	public string? Airport { get; init; }

	protected readonly List<Instruction> instructions = new();

	protected Procedure(string name) => Name = name;

	public Procedure(string name, IEnumerable<Instruction> instructions) : this(name) =>
		this.instructions = instructions.ToList();

	public virtual IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition) =>
		instructions.AsEnumerable().Select(i => i);

	public record Instruction(PathTermination Termination, IProcedureEndpoint? Endpoint, IProcedureVia? Via, SpeedRestriction Speed, AltitudeRestriction Altitude, bool OnGround = false)
	{
		public bool IsComplete(Coordinate position, Altitude altitude, decimal tolerance)
		{
			if (Termination.HasFlag(PathTermination.UntilTerminated))
				return false;

			return Endpoint?.IsConditionReached(Termination, (position, altitude, null), tolerance) ?? false;
		}
	}

	public class ProcedureJsonConverter : JsonConverter<Procedure>
	{
		public override Procedure? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			reader.Read();
			reader.Read();
			string proctype = reader.GetString() ?? throw new JsonException();
			reader.Read();
			reader.Read();

			Procedure? retval = proctype switch
			{
				"SID" => JsonSerializer.Deserialize<SID>(ref reader, options),
				"STAR" => JsonSerializer.Deserialize<STAR>(ref reader, options),
				"Approach" => JsonSerializer.Deserialize<Approach>(ref reader, options),
				_ => throw new JsonException()
			};

			reader.Read();

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return retval;
		}

		public override void Write(Utf8JsonWriter writer, Procedure value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			switch (value)
			{
				case SID s:
					writer.WriteString("ProcType", "SID");
					writer.WritePropertyName("Data");
					JsonSerializer.Serialize(writer, s, options);
					break;

				case STAR s:
					writer.WriteString("ProcType", "STAR");
					writer.WritePropertyName("Data");
					JsonSerializer.Serialize(writer, s, options);
					break;

				case Approach a:
					writer.WriteString("ProcType", "Approach");
					writer.WritePropertyName("Data");
					JsonSerializer.Serialize(writer, a, options);
					break;

				default:
					throw new NotSupportedException();
			}

			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(SIDJsonConverter))]
public class SID : Procedure
{
	private readonly Dictionary<string, Instruction[]> runwayTransitions = new();
	private readonly Instruction[] commonRoute = Array.Empty<Instruction>();
	private readonly Dictionary<string, Instruction[]> enrouteTransitions = new();

	private SID(string name, string airport, Dictionary<string, Instruction[]> runwayTransitions, Instruction[] commonRoute, Dictionary<string, Instruction[]> enrouteTransitions) : base(name) =>
		(Airport, this.runwayTransitions, this.commonRoute, this.enrouteTransitions) = (airport, runwayTransitions, commonRoute, enrouteTransitions);

	public SID(SIDLine[] lines, Dictionary<string, HashSet<Coordinate>> fixes, Dictionary<string, Aerodrome> aerodromes) : base("<EMPTY PROCEDURE>")
	{
		if (!lines.Any())
			return;

		Name = lines.First().Name;
		Airport = lines.First().Airport;
		if (lines.Any(l => l.Name != Name))
			throw new ArgumentException("The provided lines represent multiple SIDs", nameof(lines));

		Coordinate? referencePoint = null;
		if (aerodromes.ContainsKey(Airport))
			referencePoint = aerodromes[Airport].Location;
		else if (lines.Any(l => l.Endpoint is Coordinate))
			referencePoint = lines.First(l => l.Endpoint is Coordinate).Endpoint as Coordinate?;
		else if (lines.Count(l => l.Endpoint is UnresolvedWaypoint) >= 2)
		{
			UnresolvedWaypoint[] uwps =
				lines
				.Where(l => l.Endpoint is not null && l.Endpoint is UnresolvedWaypoint)
				.Select(l => (UnresolvedWaypoint)l.Endpoint!)
				.Take(2).ToArray();

			referencePoint = uwps[0].Resolve(fixes, uwps[1]);
		}

		SIDLine fix(SIDLine line)
		{
			MagneticCourse fixMagnetic(MagneticCourse mc) =>
				mc with
				{
					Variation = aerodromes.GetLocalMagneticVariation(
							(line.Endpoint, referencePoint) switch
							{
								(Coordinate c, _) => c,
								(_, null) => throw new Exception("Unable to pin magvar for SID."),
								(_, Coordinate rp) => rp
							}).Variation
				};

			if (line.Endpoint is UnresolvedWaypoint uwep)
				line = line with { Endpoint = uwep.Resolve(fixes, referencePoint) };
			if (line.Via is Arc a)
			{
				if (a.Centerwaypoint is UnresolvedWaypoint uwap)
					a = a with { Centerpoint = uwap.Resolve(fixes, referencePoint) };
				if (a.ArcTo.Variation is null)
					a = a with { ArcTo = fixMagnetic(a.ArcTo) };

				line = line with { Via = a };
			}
			else if (line.Via is Racetrack r && r.Waypoint is UnresolvedWaypoint uwrp)
				line = line with { Via = r with { Point = uwrp.Resolve(fixes, referencePoint) } };
			else if (line.Via is MagneticCourse mc && mc.Variation is null)
			{
				line = line with
				{
					Via = fixMagnetic(mc)
				};
			}

			return line;
		}

		for (int linectr = 0; linectr < lines.Length;)
		{
			SIDLine lineHead = lines[linectr];

			switch (lineHead.RouteType)
			{
				case SIDLine.SIDRouteType.RunwayTransition:
				case SIDLine.SIDRouteType.RunwayTransition_RNAV:
				case SIDLine.SIDRouteType.RunwayTransition_Vector:
					List<Instruction> rt = new();
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						rt.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					runwayTransitions.Add(lineHead.Transition, rt.ToArray());
					break;

				case SIDLine.SIDRouteType.CommonRoute:
				case SIDLine.SIDRouteType.CommonRoute_RNAV:
					List<Instruction> cr = new();
					for (; linectr < lines.Length && "25".Contains((char)lines[linectr].RouteType); ++linectr)
					{
						var line = fix(lines[linectr]);

						cr.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					commonRoute = cr.ToArray();
					break;

				case SIDLine.SIDRouteType.EnrouteTransition:
				case SIDLine.SIDRouteType.EnrouteTransition_RNAV:
				case SIDLine.SIDRouteType.EnrouteTransition_Vector:
					List<Instruction> et = new();
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						et.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					enrouteTransitions.Add(lineHead.Transition, et.ToArray());
					break;
			}
		}
	}

	public override IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition)
	{
		if (outboundTransition is not null && !enrouteTransitions.ContainsKey(outboundTransition))
			throw new ArgumentException($"Enroute transition {outboundTransition} was not found.", nameof(outboundTransition));

		if (inboundTransition is null && runwayTransitions.ContainsKey("ALL"))
			foreach (Instruction i in runwayTransitions["ALL"])
				yield return i;
		else if (inboundTransition is not null)
		{
			if (!runwayTransitions.ContainsKey(inboundTransition))
			{
				if (new[] { 'L', 'C', 'R' }.Contains(inboundTransition.Last()) && runwayTransitions.ContainsKey(inboundTransition[..^1] + "B"))
					inboundTransition = inboundTransition[..^1] + "B";
				else
					throw new ArgumentException($"Runway transition {inboundTransition} was not found.", nameof(inboundTransition));
			}

			foreach (Instruction i in runwayTransitions[inboundTransition])
				yield return i;
		}

		foreach (Instruction i in commonRoute)
			yield return i;

		if (outboundTransition is null && runwayTransitions.ContainsKey("ALL"))
			foreach (Instruction i in enrouteTransitions["ALL"])
				yield return i;
		else if (outboundTransition is not null)
			foreach (Instruction i in enrouteTransitions[outboundTransition])
				yield return i;

		yield break;
	}

	public class SIDJsonConverter : JsonConverter<SID>
	{
		public override SID? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			string name = string.Empty, airport = string.Empty;
			Dictionary<string, Instruction[]> runwayTransitions = new();
			Instruction[] commonRoute = Array.Empty<Instruction>();
			Dictionary<string, Instruction[]> enrouteTransitions = new();

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				switch (reader.GetString())
				{
					case "Name":
						reader.Read();
						name = reader.GetString() ?? throw new JsonException();
						break;

					case "Airport":
						reader.Read();
						airport = reader.GetString() ?? throw new JsonException();
						break;

					case "RunwayTransitions":
						reader.Read();
						runwayTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					case "CommonRoute":
						reader.Read();
						commonRoute = JsonSerializer.Deserialize<Instruction[]>(ref reader, options) ?? throw new JsonException();
						break;

					case "EnrouteTransitions":
						reader.Read();
						enrouteTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return new SID(name, airport, runwayTransitions, commonRoute, enrouteTransitions);
		}

		public override void Write(Utf8JsonWriter writer, SID value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			writer.WriteString("Name", value.Name);
			writer.WriteString("Airport", value.Airport);

			writer.WritePropertyName("RunwayTransitions");
			JsonSerializer.Serialize(writer, value.runwayTransitions, options);

			writer.WritePropertyName("CommonRoute");
			JsonSerializer.Serialize(writer, value.commonRoute, options);

			writer.WritePropertyName("EnrouteTransitions");
			JsonSerializer.Serialize(writer, value.enrouteTransitions, options);

			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(STARJsonConverter))]
public class STAR : Procedure
{
	private readonly Dictionary<string, Instruction[]> enrouteTransitions = new();
	private readonly Instruction[] commonRoute = Array.Empty<Instruction>();
	private readonly Dictionary<string, Instruction[]> runwayTransitions = new();
	private STAR(string name, string airport, Dictionary<string, Instruction[]> enrouteTransitions, Instruction[] commonRoute, Dictionary<string, Instruction[]> runwayTransitions) : base(name) =>
		(Airport, this.runwayTransitions, this.commonRoute, this.enrouteTransitions) = (airport, runwayTransitions, commonRoute, enrouteTransitions);

	public STAR(STARLine[] lines, Dictionary<string, HashSet<Coordinate>> fixes, Dictionary<string, Aerodrome> aerodromes) : base("<EMPTY PROCEDURE>")
	{
		if (!lines.Any())
			return;

		Name = lines.First().Name;
		Airport = lines.First().Airport;
		if (lines.Any(l => l.Name != Name))
			throw new ArgumentException("The provided lines represent multiple STARs", nameof(lines));

		Coordinate? referencePoint = null;
		if (aerodromes.ContainsKey(Airport))
			referencePoint = aerodromes[Airport].Location;
		else if (lines.Any(l => l.Endpoint is Coordinate))
			referencePoint = lines.First(l => l.Endpoint is Coordinate).Endpoint as Coordinate?;
		else if (lines.Count(l => l.Endpoint is UnresolvedWaypoint) >= 2)
		{
			UnresolvedWaypoint[] uwps =
				lines
				.Where(l => l.Endpoint is not null && l.Endpoint is UnresolvedWaypoint)
				.Select(l => (UnresolvedWaypoint)l.Endpoint!)
				.Take(2).ToArray();

			referencePoint = uwps[0].Resolve(fixes, uwps[1]);
		}

		STARLine fix(STARLine line)
		{
			MagneticCourse fixMagnetic(MagneticCourse mc) =>
				mc with
				{
					Variation = aerodromes.GetLocalMagneticVariation(
							(line.Endpoint, referencePoint) switch
							{
								(Coordinate c, _) => c,
								(_, null) => throw new Exception("Unable to pin magvar for SID."),
								(_, Coordinate rp) => rp
							}).Variation
				};

			if (line.Endpoint is UnresolvedWaypoint uwep)
				line = line with { Endpoint = uwep.Resolve(fixes, referencePoint) };
			if (line.Via is Arc a)
			{
				if (a.Centerwaypoint is UnresolvedWaypoint uwap)
					a = a with { Centerpoint = uwap.Resolve(fixes, referencePoint) };
				if (a.ArcTo.Variation is null)
					a = a with { ArcTo = fixMagnetic(a.ArcTo) };

				line = line with { Via = a };
			}
			else if (line.Via is Racetrack r && r.Waypoint is UnresolvedWaypoint uwrp)
				line = line with { Via = r with { Point = uwrp.Resolve(fixes, referencePoint) } };
			else if (line.Via is MagneticCourse mc && mc.Variation is null)
			{
				line = line with
				{
					Via = fixMagnetic(mc)
				};
			}

			return line;
		}

		for (int linectr = 0; linectr < lines.Length;)
		{
			STARLine lineHead = lines[linectr];

			switch (lineHead.RouteType)
			{
				case STARLine.STARRouteType.RunwayTransition:
				case STARLine.STARRouteType.RunwayTransition_RNAV:
					List<Instruction> rt = new();
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						rt.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					runwayTransitions.Add(lineHead.Transition, rt.ToArray());
					break;

				case STARLine.STARRouteType.CommonRoute:
				case STARLine.STARRouteType.CommonRoute_RNAV:
					List<Instruction> cr = new();
					for (; linectr < lines.Length && "25".Contains((char)lines[linectr].RouteType); ++linectr)
					{
						var line = fix(lines[linectr]);

						cr.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					commonRoute = cr.ToArray();
					break;

				case STARLine.STARRouteType.EnrouteTransition:
				case STARLine.STARRouteType.EnrouteTransition_RNAV:
					List<Instruction> et = new();
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						et.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					enrouteTransitions.Add(lineHead.Transition, et.ToArray());
					break;
			}
		}
	}

	public override IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition)
	{
		if (inboundTransition is not null && !enrouteTransitions.ContainsKey(inboundTransition))
			throw new ArgumentException($"Enroute transition {inboundTransition} was not found.", nameof(inboundTransition));

		if (outboundTransition is not null && !runwayTransitions.ContainsKey(outboundTransition))
		{
			if (new[] { 'L', 'C', 'R' }.Contains(outboundTransition.Last()) && runwayTransitions.ContainsKey(outboundTransition[..^1] + "B"))
				outboundTransition = outboundTransition[..^1] + "B";
			else
				throw new ArgumentException($"Runway transition {outboundTransition} was not found.", nameof(outboundTransition));
		}

		if (inboundTransition is null && runwayTransitions.ContainsKey("ALL"))
			foreach (Instruction i in enrouteTransitions["ALL"])
				yield return i;
		else if (inboundTransition is not null)
			foreach (Instruction i in enrouteTransitions[inboundTransition])
				yield return i;

		foreach (Instruction i in commonRoute)
			yield return i;

		if (outboundTransition is null && runwayTransitions.ContainsKey("ALL"))
			foreach (Instruction i in runwayTransitions["ALL"])
				yield return i;
		else if (outboundTransition is not null)
			foreach (Instruction i in runwayTransitions[outboundTransition])
				yield return i;

		yield break;
	}

	public class STARJsonConverter : JsonConverter<STAR>
	{
		public override STAR? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			string name = string.Empty, airport = string.Empty;
			Dictionary<string, Instruction[]> runwayTransitions = new();
			Instruction[] commonRoute = Array.Empty<Instruction>();
			Dictionary<string, Instruction[]> enrouteTransitions = new();

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				switch (reader.GetString())
				{
					case "Name":
						reader.Read();
						name = reader.GetString() ?? throw new JsonException();
						break;

					case "Airport":
						reader.Read();
						airport = reader.GetString() ?? throw new JsonException();
						break;

					case "EnrouteTransitions":
						reader.Read();
						enrouteTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					case "CommonRoute":
						reader.Read();
						commonRoute = JsonSerializer.Deserialize<Instruction[]>(ref reader, options) ?? throw new JsonException();
						break;

					case "RunwayTransitions":
						reader.Read();
						runwayTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return new(name, airport, runwayTransitions, commonRoute, enrouteTransitions);
		}

		public override void Write(Utf8JsonWriter writer, STAR value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			writer.WriteString("Name", value.Name);
			writer.WriteString("Airport", value.Airport);

			writer.WritePropertyName("EnrouteTransitions");
			JsonSerializer.Serialize(writer, value.enrouteTransitions, options);

			writer.WritePropertyName("CommonRoute");
			JsonSerializer.Serialize(writer, value.commonRoute, options);

			writer.WritePropertyName("RunwayTransitions");
			JsonSerializer.Serialize(writer, value.runwayTransitions, options);

			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(ApproachJsonConverter))]
public class Approach : Procedure
{
	private readonly Dictionary<string, Instruction[]> transitions = new();
	private readonly Instruction[] commonRoute = Array.Empty<Instruction>();

	private Approach(string name, string airport, Dictionary<string, Instruction[]> transitions, Instruction[] commonRoute) : base(name) =>
		(Airport, this.transitions, this.commonRoute) = (airport, transitions, commonRoute);

	public Approach(ApproachLine[] lines, Dictionary<string, HashSet<Coordinate>> fixes, Dictionary<string, HashSet<Navaid>> navaids, Dictionary<string, Aerodrome> aerodromes) : base("<EMPTY PROCEDURE>")
	{
		if (!lines.Any())
			return;

		Name = lines.First().Name;
		Airport = lines.First().Airport;
		if (lines.Any(l => l.Name != Name))
			throw new ArgumentException("The provided lines represent multiple IAPs", nameof(lines));

		Coordinate? referencePoint = null;
		if (aerodromes.ContainsKey(Airport))
			referencePoint = aerodromes[Airport].Location;
		else if (lines.Any(l => l.Endpoint is Coordinate))
			referencePoint = lines.First(l => l.Endpoint is Coordinate).Endpoint as Coordinate?;
		else if (lines.Count(l => l.Endpoint is UnresolvedWaypoint) >= 2)
		{
			UnresolvedWaypoint[] uwps =
				lines
				.Where(l => l.Endpoint is not null && l.Endpoint is UnresolvedWaypoint)
				.Select(l => (UnresolvedWaypoint)l.Endpoint!)
				.Take(2).ToArray();

			referencePoint = uwps[0].Resolve(fixes, uwps[1]);
		}

		ApproachLine fix(ApproachLine line)
		{
			MagneticCourse fixMagnetic(MagneticCourse mc)
			{
				decimal? var = null;
				if (line.ReferencedNavaid is not null && navaids.ContainsKey(line.ReferencedNavaid))
					 var =
						navaids[line.ReferencedNavaid]
						.OrderBy(na => na.Position.DistanceTo(referencePoint ?? (line.Endpoint is Coordinate c ? c : throw new Exception("Unable to pin magvar for IAP."))))
						.Select(na =>
							na switch
							{
								VOR v => v.MagneticVariation,
								NavaidILS ni => ni.MagneticVariation,
								ILS i => i.LocalizerCourse.Variation,
								NDB n => n.MagneticVariation,
								_ => null
							}
						)
						.FirstOrDefault(d => d is not null);

				return mc with
				{
					Variation = var ??
						aerodromes.GetLocalMagneticVariation(
							(line.Endpoint, referencePoint) switch
							{
								(Coordinate c, _) => c,
								(_, null) => throw new Exception("Unable to pin magvar for IAP."),
								(_, Coordinate rp) => rp
							}).Variation
				};
			}

			if (line.Endpoint is UnresolvedWaypoint uwep)
				line = line with { Endpoint = uwep.Resolve(fixes, referencePoint) };
			if (line.Via is Arc a)
			{
				if (a.Centerwaypoint is UnresolvedWaypoint uwap)
					a = a with { Centerpoint = uwap.Resolve(fixes, referencePoint) };
				if (a.ArcTo.Variation is null)
					a = a with { ArcTo = fixMagnetic(a.ArcTo) };

				line = line with { Via = a };
			}
			else if (line.Via is Racetrack r && r.Waypoint is UnresolvedWaypoint uwrp)
				line = line with { Via = r with { Point = uwrp.Resolve(fixes, referencePoint) } };
			else if (line.Via is MagneticCourse mc && mc.Variation is null)
			{
				line = line with
				{
					Via = fixMagnetic(mc)
				};
			}

			if (line.Via is Racetrack rt && rt.Point is null)
				throw new Exception();

			return line;
		}

		for (int linectr = 0; linectr < lines.Length;)
		{
			ApproachLine lineHead = lines[linectr];

			switch (lineHead.RouteType)
			{
				case ApproachLine.ApproachRouteType.Transition:
					List<Instruction> rt = new();
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (ApproachLine.ApproachRouteType.Transition, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						rt.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					transitions.Add(lineHead.Transition, rt.ToArray());
					break;

				default:
					List<Instruction> cr = new();
					for (; linectr < lines.Length && lines[linectr].RouteType != ApproachLine.ApproachRouteType.Transition; ++linectr)
					{
						var line = fix(lines[linectr]);

						cr.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.SpeedRestriction, line.AltitudeRestriction));
					}
					commonRoute = cr.ToArray();
					break;
			}
		}
	}

	public override IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition)
	{
		if (outboundTransition is not null)
			throw new ArgumentException($"Outbound transitions don't make sense for an IAP.", nameof(outboundTransition));

		else if (inboundTransition is not null)
		{
			if (!transitions.ContainsKey(inboundTransition))
				throw new ArgumentException($"Approach transition {inboundTransition} was not found.", nameof(inboundTransition));

			foreach (Instruction i in transitions[inboundTransition])
				yield return i;
		}

		foreach (Instruction i in commonRoute)
			yield return i;

		yield break;
	}

	public class ApproachJsonConverter : JsonConverter<Approach>
	{
		public override Approach? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			string name = string.Empty, airport = string.Empty;
			Dictionary<string, Instruction[]> transitions = new();
			Instruction[] commonRoute = Array.Empty<Instruction>();

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				switch (reader.GetString())
				{
					case "Name":
						reader.Read();
						name = reader.GetString() ?? throw new JsonException();
						break;

					case "Airport":
						reader.Read();
						airport = reader.GetString() ?? throw new JsonException();
						break;

					case "Transitions":
						reader.Read();
						transitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					case "CommonRoute":
						reader.Read();
						commonRoute = JsonSerializer.Deserialize<Instruction[]>(ref reader, options) ?? throw new JsonException();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return new(name, airport, transitions, commonRoute);
		}

		public override void Write(Utf8JsonWriter writer, Approach value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			writer.WriteString("Name", value.Name);
			writer.WriteString("Airport", value.Airport);

			writer.WritePropertyName("Transitions");
			JsonSerializer.Serialize(writer, value.transitions, options);

			writer.WritePropertyName("CommonRoute");
			JsonSerializer.Serialize(writer, value.commonRoute, options);

			writer.WriteEndObject();
		}
	}
}