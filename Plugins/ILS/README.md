# ILS (LOC only)

This plugins simulates using PID controll (with a ficticious plant) the aircarft joining the localizer of the ILS.

Command and data must be in the following form:
```
ILS <RWY threshold lat> <RWY threshold long> <RWY course (precision is required, get from charts)>
```

Example commands:
```
ILS 40.47350560 -3.53618004 322
```

# TODO:
- Add a database for ILS data
- Handle GS
	- Handle traffics intercepting too high


Alvaro (519820)

