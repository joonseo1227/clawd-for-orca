namespace Clawd.Core.Pet;

/// <summary>Things Clawd does on its own. Poses are pure functions of time.</summary>
public enum Activity { Juggle, Type, Think, Build, Groove, Sweep, Coffee, Celebrate, LookAround, Stretch, Sneeze }

public static class Activities
{
    public static readonly Activity[] All = Enum.GetValues<Activity>();

    /// <summary>Tricks for idle time. The work ones mirror agents and celebrating marks a finished
    /// task, so neither plays by chance and Clawd never looks busy or done when it isn't.</summary>
    public static readonly Activity[] Resting = [Activity.Groove, Activity.Sweep, Activity.Coffee, Activity.Stretch, Activity.Sneeze];

    public const double ThinkBulbAt = 3;
    public const double SneezeAt = 1.2;

    /// <summary>The trick's name in the Tricks menu.</summary>
    public static string Title(this Activity a) => Strings.Get("Activity_" + a);

    public static double Duration(this Activity a) => a switch
    {
        Activity.Juggle or Activity.Groove or Activity.Sweep => 6,
        Activity.Type or Activity.Coffee => 7,
        Activity.Think => 4.5,
        Activity.Build => 5,
        Activity.Celebrate or Activity.LookAround => 3,
        Activity.Stretch => 2.2,
        _ => 2,
    };

    private static double Frac(double x) => x - Math.Floor(x);

    public static Pose Pose(Activity a, double t, double dir)
    {
        var p = new Pose { Phase = t };
        var beat = (int)(t * 4) % 2 == 0;
        switch (a)
        {
            case Activity.Juggle:
            {
                p.Prop = new Prop(PropKind.Balls);
                var c = Frac(t * 3.3);
                p.ArmL = c < 0.35 ? 1 : 2;
                p.ArmR = c > 0.65 ? 1 : 2;
                p.EyeDY = -0.3;
                p.Look = Math.Cos(t * 3.3 * Math.PI * 2) * -0.6;
                break;
            }
            case Activity.Type:
            {
                p.Prop = new Prop(PropKind.Laptop);
                p.EyeDY = 0.3;
                var k = (int)(t * 10) % 2 == 0;
                p.ArmL = k ? 1.75 : 2;
                p.ArmR = k ? 2 : 1.75;
                if ((int)t % 4 == 3) { p.EyeDY = 0; p.Look = 0.8; }   // glance away now and then
                break;
            }
            case Activity.Think:
            {
                var bulb = t > ThinkBulbAt;
                p.Prop = new Prop(PropKind.Bubble, bulb);
                p.Look = bulb ? 0 : 0.8;
                p.EyeDY = bulb ? 0 : -0.35;
                p.Eyes = bulb ? Eyes.Wide : Eyes.Open;
                p.ArmR = bulb ? 1 : 1.6;
                break;
            }
            case Activity.Build:
            {
                var up = Frac(t * 2.2) < 0.5;
                p.Prop = new Prop(PropKind.Hammer, up);
                p.ArmR = up ? 1 : 2;
                p.EyeDY = 0.3;
                p.Look = -0.2;
                break;
            }
            case Activity.Groove:
                p.Prop = new Prop(PropKind.Headphones);
                p.Eyes = Eyes.Happy;
                p.Bob = beat ? 0 : -0.25;
                p.ArmL = beat ? 1.5 : 2;
                p.ArmR = beat ? 2 : 1.5;
                p.Legs = beat ? Legs.Stand : Legs.Step;
                break;
            case Activity.Sweep:
                p.Prop = new Prop(PropKind.Broom);
                p.EyeDY = 0.3;
                p.Look = dir * 0.5;
                p.ArmR = 1.6;
                p.Legs = (int)(t * 3) % 2 == 0 ? Legs.Stand : Legs.Step;
                break;
            case Activity.Coffee:
            {
                var c = t % 3;
                var sip = c > 1.6 && c < 2.6;
                p.Prop = new Prop(PropKind.Mug, sip);
                p.ArmR = sip ? 1 : 2;
                p.Eyes = sip ? Eyes.Closed : c > 2.6 ? Eyes.Happy : Eyes.Open;
                break;
            }
            case Activity.Celebrate:
                p.ArmL = beat ? 0.5 : 1;
                p.ArmR = beat ? 1 : 0.5;
                p.Eyes = Eyes.Happy;
                break;
            case Activity.LookAround:
                p.Look = t < 0.9 ? -1 : t < 1.8 ? 1 : 0;
                p.Eyes = t < 1.8 ? Eyes.Open : Eyes.Wide;
                break;
            case Activity.Stretch:
            {
                var back = t > 1.5;
                p.Squash = back ? 1 : 1 + 0.15 * Math.Min(1, t / 0.6);
                p.ArmsUp = !back;
                if (!back) { p.ArmL = 0.5; p.ArmR = 0.5; }
                p.Eyes = back ? Eyes.Happy : Eyes.Closed;
                break;
            }
            case Activity.Sneeze:
                if (t < SneezeAt)
                {
                    p.Eyes = Eyes.Closed;
                    p.Bob = -0.25 * t / SneezeAt;
                    p.Squash = 1 + 0.06 * t / SneezeAt;
                    p.Mouth = t > 0.6 ? 2 : 0;
                }
                else
                {
                    p.Eyes = Eyes.Closed;
                    p.Squash = 0.9;
                    p.ArmsUp = true;
                }
                break;
        }
        return p;
    }

    /// <summary>Yawn, nod off, flop over, then sleep.</summary>
    public static Pose SleepPose(double t)
    {
        var p = new Pose { Phase = t, Eyes = Eyes.Closed };
        if (t < 1.5)
        {
            p.Mouth = 2;
            p.ArmsUp = true;
            p.Squash = 1.05;
        }
        else if (t < 4.5)
        {
            p.Eyes = (int)(t * 1.2) % 3 == 0 ? Eyes.Open : Eyes.Closed;
            p.Bob = (int)(t * 1.25) % 2 == 0 ? 0 : 0.2;
        }
        else if (t < 5)
        {
            p.Squash = 1 - 0.35 * (t - 4.5) / 0.5;
            p.Legs = t > 4.7 ? Legs.Tucked : Legs.Stand;
        }
        else
        {
            p.Legs = Legs.Tucked;
            p.Squash = 0.65 + Math.Sin(t * 2) * 0.02;
        }
        return p;
    }

    /// <summary>The welcome window's Clawd: standing, breathing, blinking and now and then waving.</summary>
    public static Pose PortraitPose(double t)
    {
        var pose = new Pose { Phase = t, Bob = (int)(t / 0.6) % 2 == 0 ? 0 : -0.5 };
        if (t % 4 < 0.15) pose.Eyes = Eyes.Closed;
        // A short wave every eight seconds.
        var cycle = t % 8;
        if (cycle < 2)
        {
            pose.ArmR = (int)(cycle / 0.25) % 2 == 0 ? 1 : 0.5;
            pose.Eyes = Eyes.Happy;
        }
        return pose;
    }
}
