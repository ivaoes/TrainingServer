﻿using System;

namespace TrainingServer
{
	public interface IAircraft
	{
		/// <summary>The network callsign of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		string Callsign { get; }

		Flightplan Flightplan { get; }

		/// <summary>The current position of the aircraft on the WGS84 spheroid.</summary>
		Coordinate Position { get; }

		/// <summary>The present course in degrees true of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		float TrueCourse { get; }

		/// <summary>The groundspeed in knots of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		uint GroundSpeed { get; }

		/// <summary>The altimeter reading in feet above mean sea level at standard pressure of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		int Altitude { get; }

		/// <summary>The current squawk code of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		ushort Squawk { get; set; }

		/// <summary><see langword="true"/> if <see langword="this"/> <see cref="IAircraft"/> is paused, otherwise <see langword="false"/>.</summary>
		bool Paused { get; set; }

		/// <summary>Interrupts the current route. Can be resumed later with <see cref="ResumeOwnNavigation"/>.</summary>
		public void Interrupt();

		/// <summary>Skips the currently executing instruction and continues on.</summary>
		public void Continue();

		/// <summary>Resumes a route previously <see cref="Interrupt"/>ed.</summary>
		/// <returns><see langword="true"/> if a previous route was resumed, else <see langword="false"/>.</returns>
		public bool ResumeOwnNavigation();

		/// <summary>Turns to face a certain course.</summary>
		/// <param name="trueCourse">The course in degrees true to turn to.</param>
		/// <param name="turnRate">The turn rate in degrees per second.</param>
		/// <param name="turnDirection">The direction in which to turn. If <see langword="null"/>, then takes the shortest turn.</param>
		void TurnCourse(float trueCourse, float turnRate = 3f, TurnDirection? turnDirection = null);

		/// <summary>Flies to a given <see cref="Coordinate"/>.</summary>
		/// <param name="destination">The <see cref="Coordinate"/> to fly to.</param>
		/// <param name="turnRate">The turn rate in degrees per second.</param>
		/// <param name="turnDirection">The direction in which to turn. If <see langword="null"/>, then takes the shortest turn.</param>
		void FlyDirect(Coordinate destination, float turnRate = 3f, TurnDirection? turnDirection = null);

		/// <summary>Flies a given distance along the present course.</summary>
		/// <param name="distance">The distance in nautical miles to fly.</param>
		void FlyDistance(float distance);

		/// <summary>Flies for a given duration along the present course.</summary>
		/// <param name="duration">The duration to fly.</param>
		void FlyTime(TimeSpan duration);

		/// <summary>Flies an arc at the current radius from the given <paramref name="arcCenterpoint"/>.</summary>
		/// <param name="arcCenterpoint">The centerpoint/origin of the arc.</param>
		/// <param name="degreesOfArc">The number of degrees of arc (clockwise positive) to fly.</param>
		void FlyArc(Coordinate arcCenterpoint, float degreesOfArc);

		/// <summary>Flies along the present track until the <see cref="Continue"/> or <see cref="Interrupt"/> command is given.</summary>
		void FlyForever();

		/// <summary>Flies until complying with the most recently issued altitude instruction.</summary>
		void FlyAltitude();

		/// <summary>Climbs or descends as needed to comply with the given altitude restriction.</summary>
		/// <param name="minimum">The minimum altitude in feet MSL to climb to.</param>
		/// <param name="maximum">The maximum altitude in feet MSL to descend to.</param>
		/// <param name="climbRate">The vertical velocity magnitude in positive feet per second.</param>
		void RestrictAltitude(int minimum, int maximum, uint climbRate);

		/// <summary>Accelerates or decelerates as needed to comply with the given speed restriction.</summary>
		/// <param name="minimum">The minimum groundspeed in knots to accelerate to.</param>
		/// <param name="maximum">The maximum groundspeed in knots to decelerate to.</param>
		/// <param name="acceleration">The acceleration/deceleration rate in kts per second.</param>
		void RestrictSpeed(uint minimum, uint maximum, float acceleration);

		/// <summary>Immediately disconnects the aircraft.</summary>
		void Kill();

		/// <summary>Send a PM to the given recipient.</summary>
		void SendTextMessage(IServer server, string recipient, string message);
	}

	public interface IServer
	{
		IAircraft? SpawnAircraft(string callsign, Flightplan flightplan, Coordinate startingPosition, float startingCourse, uint startingSpeed, int startingAltitude);
	}

	public struct Coordinate
	{
		public double Latitude { get; set; }
		public double Longitude { get; set; }

		public override string ToString() =>
			$"({Latitude:00.0}, {Longitude:000.0})";
	}

	public struct Flightplan
	{
		public char FlightRules { get; set; }
		public char TypeOfFlight { get; set; }
		public string AircraftType { get; set; }
		public string CruiseSpeed { get; set; }
		public string DepartureAirport { get; set; }
		public DateTime EstimatedDeparture { get; set; }
		public DateTime ActualDeparture { get; set; }
		public string CruiseAlt { get; set; }
		public string ArrivalAirport { get; set; }
		public uint HoursEnRoute { get; set; }
		public uint MinutesEnRoute { get; set; }
		public uint HoursFuel { get; set; }
		public uint MinutesFuel { get; set; }
		public string AlternateAirport { get; set; }
		public string Remarks { get; set; }
		public string Route { get; set; }

		public Flightplan(char flightRules, char typeOfFlight, string aircraftType, string cruiseSpeed, string departureAirport, DateTime estimatedDeparture, DateTime actualDeparture, string cruiseAlt, string arrivalAirport, uint hoursEnRoute, uint minutesEnRoute, uint hoursFuel, uint minutesFuel, string alternateAirport, string remarks, string route) =>
			(FlightRules, TypeOfFlight, AircraftType, CruiseSpeed, DepartureAirport, EstimatedDeparture, ActualDeparture, CruiseAlt, ArrivalAirport, HoursEnRoute, MinutesEnRoute, HoursFuel, MinutesFuel, AlternateAirport, Remarks, Route) =
			(flightRules, typeOfFlight, aircraftType, cruiseSpeed, departureAirport, estimatedDeparture, actualDeparture, cruiseAlt, arrivalAirport, hoursEnRoute, minutesEnRoute, hoursFuel, minutesFuel, alternateAirport, remarks, route);
	}

	public enum TurnDirection
	{ 
		Left,
		Right
	}
}
