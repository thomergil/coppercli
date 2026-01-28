; TOOL CHANGE TEST - Tests M6 tool length offset calculation
; After tool change, approaches Z=0 carefully in stages to verify offset

G21
G90

G00 X0.00000 Y0.00000
G00 Z5.00000

G01 F100.00000
G01 Z0.00000
G04 P2.00000
G00 Z5.00000

T2 (1/8" End Mill)
M6

; Post-tool-change: careful staged approach to verify Z offset is correct
G00 X0.00000 Y0.00000
G00 Z50.00000
G04 P0.25         ; Wait 250ms at Z+5cm

G00 Z20.00000
G04 P0.25         ; Wait 250ms at Z+2cm

G00 Z5.00000
G04 P0.25         ; Wait 250ms at Z+5mm

G01 F50.00000     ; Slow approach to Z=0
G01 Z0.00000
G04 P2.00000
G00 Z5.00000

M2
