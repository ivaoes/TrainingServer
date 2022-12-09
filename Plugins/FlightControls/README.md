# Flight Controls

This plugin allows to instruct basic movements to an aircraft (climb, descend, fly direct to a FIX, turn left, turn right, squawk)... and a simple holding function.

# Commands
() -> optional parameter
[] -> obligatory parameter
{OPTION1/OPTION2} -> one of the following options must be used

## Heading commands:
```
(AFTER) TL [HDG]: turn left
(AFTER) TR [HDG]: turn right
(AFTER) FH [HDG]: fly heading
```
By adding AFTER before the command, the aircraft will complete previous instruction first.
For example in the command sequence:
```DCT TOBEK
AFTER TL 360
```
The aircraft won't start turning left to heading 360 until it has reached TOBEK.

## Altitude commands:
```
A [FL]: altitude
C [FL]: climb
D [FL]: descend
```
By adding AFTER before the command, the aircraft will complete previous instruction first.

For example in the command sequence:
```DCT TOBEK
AFTER D 050
```
The aircraft won't start descending to 5000ft until it has reached TOBEK.

## Holding
```
HOLD [{RIGHT/LEFT}] [INBOUND COURSE] [FIX]: holding
```
Example command: `HOLD RIGHT 047 TOBEK`

Note: if you are not using Niko's FixRewriter plugin, FIX parameter must be given in the following format: lat/lon. Example: 41.232/-2.343

Note 2: if you are using Niko's FixRewriter plugin, you can save holdings to speed up the process. Example:
By adding `TOBEKHOLD RIGHT 047 40.19615545/-3.42444695` to your `fixes.fix` configuration file, you can now instruct a traffic to the standard holding over TOBEK by sending `HOLD TOBEKHOLD`

## Direct
```
(AFTER) DCT [FIX]: fly direct to a FIX
```

By adding AFTER before the command, the aircraft will complete previous instruction first.
```DCT TOBEK
AFTER DCT NVS
```
The aircraft won't start flying to NVS until it has reached TOBEK.

Note: if you are not using Niko's FixRewriter plugin, FIX parameter must be given in the following format: lat/lon. Example: 41.232/-2.343

## Speed control
```
SPD [SPEED IN KNOTS]: speed
```

## Squawk control
```
SQK [SQUAWK]: squawk
```

## Delete
```
DIE: kills the aircraft
```

Maintainer: 605126