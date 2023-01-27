using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CIFPReader;

[JsonConverter(typeof(AltitudeJsonConverter))]
public abstract record Altitude(int Feet)
{
	public abstract AltitudeAGL ToAGL(int groundElevation);
	public abstract AltitudeMSL ToMSL();

	public virtual bool Equals(Altitude? other) => other is not null && other.GetHashCode() == GetHashCode();
	public override int GetHashCode() => ToMSL().Feet;

	public static readonly Altitude MinValue = new AltitudeMSL(int.MinValue);
	public static readonly Altitude MaxValue = new AltitudeMSL(int.MaxValue);

	public static Altitude operator +(Altitude left, Altitude right) =>
		left switch
		{
			AltitudeMSL am => am with { Feet = am.Feet + right.ToMSL().Feet },
			AltitudeAGL ag => ag with { Feet = ag.Feet + right.ToAGL(ag.GroundElevation ?? 0).Feet },
			_ => throw new NotImplementedException()
		};

	public static Altitude operator -(Altitude left, Altitude right) =>
		left switch
		{
			AltitudeMSL am => am with { Feet = am.Feet - right.ToMSL().Feet },
			AltitudeAGL ag => ag with { Feet = ag.Feet - right.ToAGL(ag.GroundElevation ?? 0).Feet },
			_ => throw new NotImplementedException()
		};

	public static bool operator <(Altitude left, Altitude right) => left.GetHashCode() < right.GetHashCode();
	public static bool operator <=(Altitude left, Altitude right) => left.GetHashCode() <= right.GetHashCode();
	public static bool operator >(Altitude left, Altitude right) => left.GetHashCode() > right.GetHashCode();
	public static bool operator >=(Altitude left, Altitude right) => left.GetHashCode() >= right.GetHashCode();

	public class AltitudeJsonConverter : JsonConverter<Altitude>
	{
		public override Altitude? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			new AltitudeMSL(reader.GetInt32());

		public override void Write(Utf8JsonWriter writer, Altitude value, JsonSerializerOptions options) =>
			writer.WriteNumberValue(value.Feet);
	}
}

public record AltitudeAGL(int Feet, int? GroundElevation) : Altitude(Feet)
{
	public override AltitudeAGL ToAGL(int groundElevation) =>
		GroundElevation is null
		? new(Feet, GroundElevation)
		: ToMSL().ToAGL(groundElevation);

	public override AltitudeMSL ToMSL() =>
		GroundElevation is null
		? throw new Exception("Cannot convert AGL to MSL without knowing ground elevation")
		: new(Feet + GroundElevation.Value);

	public virtual bool Equals(AltitudeAGL? other) =>
		Feet == other?.Feet && GroundElevation == other.GroundElevation;
	public override int GetHashCode() => base.GetHashCode();
}

public record AltitudeMSL(int Feet) : Altitude(Feet)
{
	public override AltitudeAGL ToAGL(int groundElevation) => new(Feet - groundElevation, groundElevation);
	public override AltitudeMSL ToMSL() => new(this);
}

public record FlightLevel(int FL) : AltitudeMSL(FL * 100) { }