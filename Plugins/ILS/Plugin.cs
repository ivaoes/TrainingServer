﻿using System.Diagnostics.CodeAnalysis;
using System.Runtime.ConstrainedExecution;
using System.Text.RegularExpressions;
using TrainingServer;
using TrainingServer.Extensibility;

namespace ILS;

public class Plugin : IPlugin
{
#if DEBUG
	public string FriendlyName => "ILS (DEBUG)";
#else
	public string FriendlyName => "ILS";
#endif
	public string Maintainer => "Alvaro (519820)";

    double DIST_STEP = 0.001;

    private readonly Regex _ils;

	public Plugin()
	{

		string[] regexes = new[]
		{
            @"ILS\s(?<lat>[+-]?\d+(\.\d+)?)[ /;](?<lon>[+-]?\d+(\.\d+)?);?\s*(?<hdg>\d+(.\d+)?)",
		};


		_ils = new(regexes[0], RegexOptions.IgnoreCase);
	}

	public bool CheckIntercept(string aircraftCallsign, string sender, string message)
	{
		return _ils.IsMatch(message);
	}

    double integral_error;
    double error_last;
    double kp = 2.5;
    double ki = 0.16;
    double kd = 0.05;
    double target;

    public double controller(double pos)
    {
        double error = pos - target;

        this.integral_error += error * DIST_STEP;
        double derivative_error = (error - error_last) / DIST_STEP;
        double output = this.kp * error + this.ki * integral_error + this.kd * derivative_error;
        this.error_last = error;

        return output;
    }

    private double brng_from_vec(double lat1, double lon1, double lat2, double lon2)
	{
        double lat1_rad = lat1 * Math.PI / 180;
		double lat2_rad = lat2 * Math.PI / 180;
		double delta1 = (lat2 - lat1) * Math.PI / 180;
		double delta2 = (lon2 - lon1) * Math.PI / 180;

		double y = Math.Sin(delta2) * Math.Cos(lat2_rad);
		double x = Math.Cos(lat1_rad) * Math.Sin(lat2_rad) - Math.Sin(lat1_rad) * Math.Cos(lat2_rad) * Math.Cos(delta2);


        double brng = Math.Atan2(y, x) * 180 / Math.PI;

		return brng < 0 ? brng + 360 : brng;
	}

	private (double, double) plant(double hdg1, double lat1, double lon1, double d)
	{
		// Convert all to radians
		lat1 = lat1 * Math.PI / 180;
		lon1 = lon1 * Math.PI / 180;
        hdg1 = hdg1 * Math.PI / 180;
		d = d * Math.PI / 180;

        // See https://edwilliams.org/avform147.htm#LL
        double lat, lon;

        lat = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(hdg1));
		lon = lon1 + Math.Atan2(Math.Sin(hdg1) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat));

        lat = lat * 180 / Math.PI;
		lon = lon * 180 / Math.PI;	

		return (lat, lon);
    }

	private double turn(double hdg, double init_hdg, char turn_dir)
	{
		if ((hdg > init_hdg) && (turn_dir == 'L'))
		{
			return init_hdg;
		}

        else if ((hdg < init_hdg) && (turn_dir == 'R'))
        {
            return init_hdg;
        }

        return hdg;
	}

    public string? MessageReceived(IAircraft aircraft, string sender, string message)
	{
		
		double rwy_offset;
        (double, double) temp_point = (aircraft.Position.Latitude, aircraft.Position.Longitude);

		bool first = true;
        char turn_dir;
		int additional = 5;

		double acft_hdg = aircraft.TrueCourse;
		double init_hdg = acft_hdg;

        List<(double, double)> points = new List<(double, double)>();
        points.Add((aircraft.Position.Latitude, aircraft.Position.Longitude));

        Match match = _ils.Match(message);

        // Load and vectorize all data
        double[] rwy_point = new double[2];
        rwy_point[0] = double.Parse(match.Groups["lat"].Value);
        rwy_point[1] = double.Parse(match.Groups["lon"].Value);

		this.target = float.Parse(match.Groups["hdg"].Value);

        // 0 degree fix
        if (acft_hdg + 90 < this.target)
            acft_hdg += 360;
        else if (this.target + 90 < acft_hdg)
            this.target += 360;

        if ((acft_hdg > brng_from_vec(points[0].Item1, points[0].Item2, rwy_point[0], rwy_point[1])) && brng_from_vec(points[0].Item1, points[0].Item2, rwy_point[0], rwy_point[1]) > this.target)
            turn_dir = 'L';
        else if ((acft_hdg < brng_from_vec(points[0].Item1, points[0].Item2, rwy_point[0], rwy_point[1])) && brng_from_vec(points[0].Item1, points[0].Item2, rwy_point[0], rwy_point[1]) < this.target)
            turn_dir = 'R';
        else
            return "Already passed the loc";

        // PID
        for (int i = 0; i < 1000; i++)
        {
			rwy_offset = brng_from_vec(points[points.Count-1].Item1, points[points.Count-1].Item2, rwy_point[0], rwy_point[1]);


            acft_hdg = this.turn(this.controller(rwy_offset) + acft_hdg, init_hdg, turn_dir);

            temp_point = this.plant(acft_hdg, points[points.Count-1].Item1, points[points.Count-1].Item2, DIST_STEP);

			points.Add(temp_point);

			if (first){
				first = false;
			}

			else
			{
                if (Math.Abs(brng_from_vec(points[points.Count-1].Item1, points[points.Count-1].Item2, rwy_point[0], rwy_point[1]) - this.target) < 0.2)
				{
                    if (additional == 0) { break; }
                    else { additional = additional - 1; }
                }
					

				else if (Math.Abs(brng_from_vec(points[points.Count-3].Item1, points[points.Count-3].Item2, points[points.Count-2].Item1, points[points.Count-2].Item2) - brng_from_vec(points[points.Count-2].Item1, points[points.Count-2].Item2, points[points.Count-1].Item1, points[points.Count-1].Item2)) < 1)
				{
                    points.RemoveAt(points.Count - 2);
                }
            }


		}

		for (int i = 0; i < points.Count; i++)
		{ 
			Console.WriteLine(points[i]);
            aircraft.FlyDirect(new() { Latitude = points[i].Item1, Longitude = points[i].Item2 });
        }

        aircraft.FlyDirect(new() { Latitude = rwy_point[0], Longitude = rwy_point[1] });

        Console.WriteLine("Done");
        return "DONE";
	}
}