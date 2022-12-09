using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

using TrainingServer;
using TrainingServer.Extensibility;

namespace FlightControls;

public class Plugin : IPlugin
{
#if DEBUG
	public string FriendlyName => "Flight Controls (DEBUG)";
#else
	public string FriendlyName => "Flight Controls";
#endif
    public string Maintainer => "Alex (605126)";

	private readonly Regex _headingRegex, _altitudeRegex, _speedRegex, _squawkRegex, _turnLeftRegex, _turnRightRegex, _directRegex, _holdingRegex;

    private static Dictionary<string, bool> aircraftsHolding = new();

    enum Action
    {
		heading, turnLeft, turnRight, altitude, speed, direct, holding
    }

    private class HoldingPattern
    {
        public IAircraft aircraft;
        public float inboundCourse;
        public float outboundCourse;
        public string inboundFix;
        public Coordinate inboundCoordinates;
        public TurnDirection turnDirection;
    }

    readonly RegexOptions rxo = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    public Plugin()
	{

		string[] regexes = new[]
		{
            @"^(AFTER\s(FH|T)|(FH|T))\s*(?<hdg>\d+(\.\d+)?)",
            @"^(AFTER\sTL|TL)\s*(?<tl>\d+(\.\d+)?)",
            @"^(AFTER\sTR|TR)\s*(?<tr>\d+(\.\d+)?)",
            @"^(EX\s[ACD]|[ACD])\s*(?<alt>\d+)",
			@"^SPD\s*(?<spd>\d+)",
			@"^SQK?\s*(?<sqk>\d{4})",
            @"^(AFTER\sDCT|DCT)\s*(?<dct>\w+(\.\w+)(\/.\w+)(\.\w+)?)",
            @"^(HOLD\sRIGHT|HOLD\sLEFT)\s*(?<ibdCourse>\d+(\.\d+)?)\s(?<hold>\w+(\.\w+)(\/.\w+)(\.\w+)?)"
        };

		if (File.Exists("commands.re") && File.ReadAllLines("commands.re").Length >= regexes.Length)
			regexes = File.ReadAllLines("commands.re");
		else
			File.WriteAllLines("commands.re", regexes);

		_headingRegex	= new(regexes[0], rxo);
		_turnLeftRegex	= new(regexes[1], rxo);
        _turnRightRegex = new(regexes[2], rxo);
		_altitudeRegex	= new(regexes[3], rxo);
		_speedRegex		= new(regexes[4], rxo);
		_squawkRegex	= new(regexes[5], rxo);
        _directRegex    = new(regexes[6], rxo);
        _holdingRegex   = new(regexes[7], rxo);
	}

	private bool TryBreakUp(string message, out object[] fragments, out ushort? squawk)
	{
		List<Tuple<Action, object>> frags = new();
		squawk = null;

		while (!string.IsNullOrWhiteSpace(message))
		{
			while (message.Any() && char.IsPunctuation(message[0]))
				message = message[1..];
			message = message.Trim();

            Match match;
            switch (message)
            {
				case var _ when _headingRegex.IsMatch(message):
                    match = _headingRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.heading, float.Parse(match.Groups["hdg"].Value)));
                    message = message[match.Length..];
					break;

				case var _ when _turnLeftRegex.IsMatch(message):
                    match = _turnLeftRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.turnLeft, float.Parse(match.Groups["tl"].Value)));
                    message = message[match.Length..];
					break;

				case var _ when _turnRightRegex.IsMatch(message):
                    match = _turnRightRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.turnRight, float.Parse(match.Groups["tr"].Value)));
                    message = message[match.Length..];
					break;

                case var _ when _altitudeRegex.IsMatch(message): 
                    match = _altitudeRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.altitude, int.Parse(match.Groups["alt"].Value) * 100));
                    message = message[match.Length..];
                    break;

                case var _ when _speedRegex.IsMatch(message):
                    match = _speedRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.speed, uint.Parse(match.Groups["spd"].Value)));
                    message = message[match.Length..];
                    break;

                case var _ when _squawkRegex.IsMatch(message):
                    match = _squawkRegex.Match(message);
                    squawk = ushort.Parse(match.Groups["sqk"].Value);
                    message = message[match.Length..];
                    break;

                case var _ when _directRegex.IsMatch(message):
                    match = _directRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.direct, match.Groups["dct"].Value));
                    message = message[match.Length..];
                    break;

                case var _ when _holdingRegex.IsMatch(message):
                    match = _holdingRegex.Match(message);

                    TurnDirection turnDirection = TurnDirection.Right;
                    if (new Regex(@"HOLD\sLEFT\s", rxo).IsMatch(message))
                        turnDirection = TurnDirection.Left;

                    HoldingPattern holding = new();
                    holding.turnDirection = turnDirection;
                    holding.inboundCourse = float.Parse(match.Groups["ibdCourse"].Value);
                    holding.inboundFix = match.Groups["hold"].Value;

                    frags.Add(new Tuple<Action, object>(Action.holding, holding));
                    message = message[match.Length..];
                    break;

                default:
                    fragments = frags.ToArray();
                    return false;
            }
        }

		fragments = frags.ToArray();
		return frags.Any() || squawk is not null;
	}

	public bool CheckIntercept(string aircraftCallsign, string sender, string message) =>
		message.Trim().Equals("DIE", StringComparison.InvariantCultureIgnoreCase) || TryBreakUp(message, out _, out _);

    public void InterruptHolding(IAircraft aircraft)
    {
        try
        {
            aircraftsHolding[aircraft.Callsign] = false;
        }
        catch
        {
            // fail silently
        }
    }

    static async Task<bool> HoldingLoop(HoldingPattern holding)
    {
        var inboundCourse = holding.inboundCourse;
        var inboundCoordinates = holding.inboundCoordinates;
        var turnDirection = holding.turnDirection;
        var aircraft = holding.aircraft;

        var outboundCourse = inboundCourse + 180;
        const int legTime = 60 * 1000; // 1 minutes in ms

        if (outboundCourse > 360.0)
            outboundCourse -= 360;

        // initial turn to the FIX should be at normal turn rate
        var inboundRate = 3f;

        while (aircraftsHolding[aircraft.Callsign])
        {
            // fly inbound
            aircraft.FlyDirect(inboundCoordinates, inboundRate);

            // set holding turn rate for further iterations
            inboundRate = 1.5f;

            // enforce inbound course, I know this is cheating
            // feel free to add a system to make a proper enter on the holding
            aircraft.TurnCourse(inboundCourse, 360f);

            // add outbound turn to the queue
            aircraft.TurnCourse(outboundCourse, 1.5f, turnDirection);

            // give some time to update the present course
            Thread.Sleep(5000);

            // wait until aircraft is heading outbound
            while ((int)aircraft.TrueCourse != (int)outboundCourse && aircraftsHolding[aircraft.Callsign])
            {
                Thread.Sleep(2000);
            }

            // stop holding
            if (!aircraftsHolding[aircraft.Callsign])
                break;

            Thread.Sleep(legTime);
        }

        // pop aircraft from holding list
        aircraftsHolding.Remove(aircraft.Callsign);

        return true;
    }

	public string? MessageReceived(IAircraft aircraft, string sender, string message)
	{
		if (message.Trim().Equals("DIE", StringComparison.InvariantCultureIgnoreCase))
		{
			aircraft.Kill();
			return "Goodbye!";
		}

		_ = TryBreakUp(message, out object[] fragments, out ushort? squawk);
		List<string> msgBacks = new();

		if (squawk is not null)
		{
			try { aircraft.Squawk = squawk.Value; msgBacks.Add("Squawking"); msgBacks.Add(squawk.Value.ToString()); }
			catch { System.Diagnostics.Debug.WriteLine("Invalid squawk " + squawk); }
		}
		else
			msgBacks.Add("Flying");

		foreach (Tuple<Action, object> frag in fragments)
			switch (frag.Item1)
			{
				case Action.heading:
                    if (!new Regex(@"AFTER\sFH\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHolding(aircraft);

                    aircraft.TurnCourse((float)frag.Item2);
					msgBacks.Add($"heading {(float)frag.Item2:000.00}");
					break;
                
                case Action.turnLeft:
					if (!new Regex(@"AFTER\sTL\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHolding(aircraft);

                    aircraft.TurnCourse((float)frag.Item2, 3f, TurnDirection.Left);
					msgBacks.Add($"heading {(float)frag.Item2:000.00}");
					break;
                
                case Action.turnRight:
                    if (!new Regex(@"AFTER\sTR\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHolding(aircraft);

                    aircraft.TurnCourse((float)frag.Item2, 3f, TurnDirection.Right);
					msgBacks.Add($"heading {(float)frag.Item2:000.00}");
					break;

                case Action.altitude:
                    Random rnd = new();
                    int climbVerticalSpeed = rnd.Next(1000, 2000), descentVerticalSpeed = rnd.Next(800, 1800);

					// expedite descent/clb
                    if (new Regex(@"EX\s[CD]\s", rxo).IsMatch(message)) {
                        climbVerticalSpeed = 2000; 
                        descentVerticalSpeed = 2500;
                    }

                    var alt = (int)frag.Item2;
                    aircraft.RestrictAltitude(alt, alt, (uint)(aircraft.Altitude > alt ? descentVerticalSpeed : climbVerticalSpeed));
					msgBacks.Add($"altitude {alt / 100:000}");
					break;

				case Action.speed:
                    var spd = (uint)frag.Item2;
                    aircraft.RestrictSpeed(spd, spd, aircraft.GroundSpeed > spd ? 2.5f : 5f);
					msgBacks.Add($"speed {spd:000}");
					break;

                case Action.direct:
                    if (!new Regex(@"AFTER\sDCT\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHolding(aircraft);

                    var dct = (string)frag.Item2;
                    string[] elems = dct.Split('/');
                    aircraft.FlyDirect(new() { Latitude = double.Parse(elems[0]), Longitude = double.Parse(elems[1]) });
                    msgBacks.Add($"direct to {dct}");
                    break;

                case Action.holding:


                    var holdingObj = (HoldingPattern)frag.Item2;
                    holdingObj.aircraft = aircraft;

                    var coords = holdingObj.inboundFix.Split('/');

                    // set up coordinates
                    holdingObj.inboundCoordinates.Latitude = double.Parse(coords[0]);
                    holdingObj.inboundCoordinates.Longitude = double.Parse(coords[1]);

                    // start holding
                    aircraftsHolding.Add(aircraft.Callsign, true);
                    var holdingTask = Task.Run(() => HoldingLoop(holdingObj));
                    

                    msgBacks.Add($"holding over {holdingObj.inboundFix}");
                    break;

                default:
					System.Diagnostics.Debug.WriteLine("Unknown fragment " + frag.ToString());
					break;
			}

		return msgBacks[0] + " " + string.Join(", then ", msgBacks.Skip(1));
	}
}