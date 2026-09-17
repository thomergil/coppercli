#!/usr/bin/env sh
# Mechanizable checks for the coppercli architecture contract.
# Run from the repository root:  sh .architecture/rules/check-layering.sh
# Exit 0 = clean, 1 = at least one violation. Each violation prints its rule name.

status=0
fail() { echo "VIOLATION [$1] $2"; status=1; }

# Every grep below tolerates a missing path, so without this the script exits 0 from a
# directory holding none of the code it checks. Naming the directories is not enough: empty
# ones satisfy that, so each file a rule reads is named too.
for required in coppercli coppercli.Core coppercli.Tests \
        coppercli/Menus coppercli/Helpers coppercli/WebServer/wwwroot/js; do
    if [ ! -d "$required" ]; then
        echo "VIOLATION [check-runs-at-the-repository-root] $required is missing; run this from the repository root"
        exit 1
    fi
done

# MachineWait.cs is read by iterating the methods it declares, so an empty one means an
# empty loop and no violation.
if [ "$(grep -cE '(public|internal) static' coppercli.Core/Controllers/MachineWait.cs 2>/dev/null)" -lt 10 ]; then
    echo "VIOLATION [check-runs-at-the-repository-root] coppercli.Core/Controllers/MachineWait.cs declares almost nothing; a-new-distinction-lands-with-its-callers would check nothing"
    exit 1
fi

for required in ARCHITECTURE.md \
        coppercli.Core/Controllers/MachineWait.cs \
        coppercli.Core/coppercli.Core.csproj \
        coppercli/AppState.cs \
        coppercli/Helpers/MenuHelpers.cs \
        coppercli/Menus/MainMenu.cs \
        coppercli/WebServer/WebConstants.cs \
        coppercli/WebServer/CncWebServer.cs \
        coppercli/WebServer/wwwroot/index.html \
        coppercli/WebServer/wwwroot/js/constants.js \
        coppercli/WebServer/wwwroot/js/helpers.js; do
    if [ ! -s "$required" ]; then
        echo "VIOLATION [check-runs-at-the-repository-root] $required is missing or empty; run this from the repository root"
        exit 1
    fi
done

# --- rule: core-is-platform-independent -----------------------------------
# coppercli.Core must never reference the app project, nor any UI/web/host type.
if grep -q "coppercli\.csproj" coppercli.Core/coppercli.Core.csproj 2>/dev/null; then
    fail core-is-platform-independent "coppercli.Core.csproj references the app project"
fi

if grep -rlE "using (Spectre\.Console|System\.Net\.Http|System\.Net\.WebSockets)|namespace coppercli\.(Menus|WebServer|Helpers)" \
        --include="*.cs" coppercli.Core/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -q .; then
    fail core-is-platform-independent "coppercli.Core imports a UI or web-host dependency"
fi

# --- rule: api-paths-are-constants ----------------------------------------
# No literal /api/ path inside a fetch call; JS reads them from constants.js.
if grep -rn "fetch(['\"\`]/api" --include="*.js" coppercli/WebServer/wwwroot/js/ 2>/dev/null \
        | grep -v constants.js | grep -q .; then
    fail api-paths-are-constants "a fetch() call hardcodes an /api path instead of using an API_* constant"
fi

# --- rule: ws-message-types-updated-in-four-places ------------------------
# Every shared name declared in constants.js must also be validated in helpers.js: the
# WebSocket message types, the client-to-server commands, and the height-map outcomes. One
# declared and not validated is a mismatch that only shows up at runtime.
if [ -f coppercli/WebServer/wwwroot/js/constants.js ] && [ -f coppercli/WebServer/wwwroot/js/helpers.js ]; then
    for prefix in MSG_TYPE_ CMD_ ZEROED_; do
        declared=$(grep -o "\\b$prefix[A-Z_]*" coppercli/WebServer/wwwroot/js/constants.js | sort -u)
        # Matched at the check() call, not the import: importing a name proves nothing
        # about whether validateConstants compares it.
        validated=$(grep -vE '^ *(//|\*|/\*)' coppercli/WebServer/wwwroot/js/helpers.js \
            | grep -oE "check\( *$prefix[A-Z_]*" \
            | grep -oE "$prefix[A-Z_]*" | sort -u)
        missing=$(echo "$declared" | grep -vxF "$validated" 2>/dev/null)
        if [ -n "$missing" ]; then
            fail ws-message-types-updated-in-four-places "declared in constants.js but not validated in helpers.js: $(echo "$missing" | tr '\n' ' ')"
        fi
    done

    # The C# half, compared by value rather than by count: a rename on one side leaves the
    # totals equal while the two sides name different things.
    for pair in "WsMessageType:MSG_TYPE_" "WsCmd:CMD_"; do
        cs_prefix=${pair%%:*}
        js_prefix=${pair#*:}
        cs_values=$(grep -oE "const string $cs_prefix[A-Za-z]* *= *\"[^\"]*\"" \
            coppercli/WebServer/WebConstants.cs | grep -oE '"[^"]*"' | tr -d '"' | sort -u)
        js_values=$(grep -oE "^export const $js_prefix[A-Z_]* *= *'[^']*'" \
            coppercli/WebServer/wwwroot/js/constants.js | grep -oE "'[^']*'" | tr -d "'" | sort -u)
        if [ "$cs_values" != "$js_values" ]; then
            fail ws-message-types-updated-in-four-places "the $cs_prefix* wire names in WebConstants.cs and the $js_prefix* names in constants.js are not the same set"
        fi
    done
fi

# --- rule: monotonic-time-and-event-counts --------------------------------
# A wait measured against the wall clock ends at once, or never, when NTP or daylight
# saving steps it. Displayed times are not timeouts and are left alone.
if grep -rnE 'DateTime(Offset)?\.(Now|UtcNow) *[<>]|[<>]=? *DateTime(Offset)?\.(Now|UtcNow)|DateTime(Offset)?\.(Now|UtcNow)\.(Add|Subtract|CompareTo)|DateTime(Offset)?\.(Now|UtcNow) *-|- *DateTime(Offset)?\.(Now|UtcNow)|Environment\.TickCount[^6]' \
        --include="*.cs" coppercli/ coppercli.Core/ coppercli.Tests/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -q .; then
    fail monotonic-time-and-event-counts "a timeout is measured against the wall clock; use Stopwatch or Environment.TickCount64"
fi

# Core displays nothing, so it has no reason to read a wall clock. The app layer may,
# for a timestamp it prints.
if grep -rn 'DateTime\.Now\|DateTime\.UtcNow' --include="*.cs" coppercli.Core/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -q .; then
    fail monotonic-time-and-event-counts "coppercli.Core reads the wall clock; it measures elapsed time, so use Environment.TickCount64"
fi

# --- rule: one-handler-per-control ----------------------------------------
# onclick and addEventListener are separate slots and both fire, so a control bound through
# each runs its handler twice on one tap.
html=coppercli/WebServer/wwwroot/index.html
jsdir=coppercli/WebServer/wwwroot/js
if [ -f "$html" ] && [ -d "$jsdir" ]; then
    doubled=""
    # Both slots counted the same way: named inline, or through a local holding the element.
    # An id passed in as an argument cannot be resolved by grep and is not covered.
    for id in $(grep -o 'id="[A-Za-z0-9_-]*"' "$html" | sed 's/id="//; s/"$//' | sort -u); do
        listens=$(grep -rn "['\"]$id['\"]" "$jsdir" 2>/dev/null | grep -c "addEventListener")
        clicks=$(grep -rn "['\"]$id['\"]" "$jsdir" 2>/dev/null | grep -c '\.onclick')

        # The page can hold a slot too, and an onclick attribute there fires alongside any
        # listener the modules add.
        grep -qE "id=\"$id\"[^>]*onclick=" "$html" && clicks=$((clicks + 1))

        for f in "$jsdir"/*.js; do
            for v in $(sed -n "s/.*[^A-Za-z0-9_]\([A-Za-z_][A-Za-z0-9_]*\) *= *\(document\.getElementById\|document\.querySelector\|\$\)(['\"]#\{0,1\}$id['\"]).*/\1/p" "$f" 2>/dev/null | sort -u); do
                # Only a name this file binds to one id, or a handler on one would count
                # against every id the name ever holds.
                bindings=$(grep -cE "[^A-Za-z0-9_]$v *= *(document\.(getElementById|querySelector)|\$)\(" "$f" 2>/dev/null)
                [ "$bindings" -eq 1 ] || continue

                listens=$((listens + $(grep -c "$v\.addEventListener" "$f" 2>/dev/null)))
                clicks=$((clicks + $(grep -c "$v\.onclick" "$f" 2>/dev/null)))
            done
        done

        [ "$listens" -gt 0 ] && [ "$clicks" -gt 0 ] && doubled="$doubled $id"
    done
    if [ -n "$doubled" ]; then
        fail one-handler-per-control "bound by both addEventListener and onclick, so one tap fires twice:$doubled"
    fi
fi

# --- rule: the-browser-draws-what-it-was-handed ---------------------------
# A question about the machine is answered once, in Core, and sent as a value. The raw status
# word is for display only: a browser that derives an answer from it holds a second
# definition, and the two then disagree. This grep finds the comparisons a browser would
# write; WebServerSequenceTests and coppercli.Tests/browser/ check the behaviour.
if grep -rnE "STATUS_(RUN|HOLD|IDLE|ALARM|DOOR|ALARM_PREFIX)|\.status *[=!]==? *['\"](Run|Hold|Idle|Alarm|Door)|\.status(\.[A-Za-z]+)? *\.(startsWith|includes) *\( *['\"](Run|Hold|Idle|Alarm|Door)|machineActivity *[=!]==? *['\"]|case +['\"](Run|Hold|Idle|Alarm|Door)['\"]" \
        --include="*.js" coppercli/WebServer/wwwroot/js/ 2>/dev/null \
        | grep -v constants.js | grep -q .; then
    fail the-browser-draws-what-it-was-handed "the browser decides a machine question from the raw status word; decide it in Core and ship the answer"
fi

# --- rule: an-element-the-code-writes-to-exists ----------------------------
# getElementById returns null for an id the page does not have. A write guarded with
# `if (el)` then does nothing silently; an unguarded one throws. There is no compile step,
# and the C# tests do not load the page.
absent=""
# Every id the code spells out, through getElementById, $() or a #selector. An id held in a
# constant, or assembled at run time, cannot be resolved by grep and is not covered.
for id in $(grep -rhoE "getElementById\(['\"][a-zA-Z0-9_-]+['\"]\)|\\\$\(['\"][a-zA-Z0-9_-]+['\"]\)|querySelector\(['\"]#[a-zA-Z0-9_-]+['\"]\)" \
        "$jsdir" 2>/dev/null | grep -oE "['\"][a-zA-Z0-9_-]+['\"]" | tr -d "\"'" | sort -u); do
    grep -q "id=\"$id\"" "$html" || absent="$absent $id"
done
if [ -n "$absent" ]; then
    fail an-element-the-code-writes-to-exists "the browser writes to ids the page does not have, so the writes do nothing:$absent"
fi

# --- rule: ui-text-is-a-constant ------------------------------------------
# Text a person reads is named once in constants.js, never written at the point of use.
# Backticks are matched too. A literal starting with a placeholder is matched by the space
# before a word inside it: sentences have spaces between words, assembled element ids do not.
# A literal opening with a tag is markup, whose own text comes from constants.
if grep -rnE "\.(textContent|innerHTML|innerText|title|value|placeholder|ariaLabel) *=[^=]*['\"\`] *[A-Za-z]|\.(textContent|innerHTML) *= *\`[^<\`][^\`]* [A-Za-z]|show(Error|Info|Confirm|Warning)\(['\"\`][A-Za-z]|setText\([^,]*, *['\"\`][A-Za-z]|setAttribute\( *['\"](title|aria-label)['\"], *['\"\`][A-Za-z]" \
        --include="*.js" coppercli/WebServer/wwwroot/js/ 2>/dev/null \
        | grep -v constants.js | grep -q .; then
    fail ui-text-is-a-constant "a string a person reads is written at the point of use; name it in constants.js"
fi

# --- rule: no-exception-text-on-screen ------------------------------------
# An exception message names files, offsets and types the operator cannot act on. It goes to
# the log, and the screen gets a sentence about what failed. Matches the catch-variable names
# this codebase uses; on the C# side WriteFailure is the one path that answers a caught
# exception.
if grep -rnE "show(Error|Info|Confirm|Warning)\(.*(err|error|e|ex|exc)\.(message|stack)|show(Error|Info|Confirm|Warning)\( *(String\(|[A-Za-z_]*\.toString\(\))|\.(textContent|innerHTML) *= *[A-Za-z_]*(err|error|exc)[A-Za-z_]*\.message" \
        --include="*.js" coppercli/WebServer/wwwroot/js/ 2>/dev/null | grep -q .; then
    fail no-exception-text-on-screen "an exception message is shown to the operator; log it and show a sentence they can act on"
fi

# The same on the C# side. WriteFailure answers the browser and MenuHelpers.ShowFailure the
# terminal; both log the exception and show a sentence. ControllerError is not an exception:
# it is the run's own wording, written for the operator.
#
# Any call that writes to the screen, and any catch variable this codebase uses, whether the
# text comes from .Message, .ToString() or the exception interpolated whole. Not caught: a
# message first copied into a local, because grep cannot follow it. ControllerError.Message is
# the run's own wording and goes to the screen through MenuHelpers.ShowRunError;
# ControllerConstants.ShowableMessage is the one sanctioned way to pass an exception's words on.
screen_call='(MarkupLine|MarkupLineInterpolated|Markup|WriteLine|Write|WriteException|AddRow|ShowError|ShowFailure[A-Za-z]*|ShowOverlay[A-Za-z]*|WriteLineTruncated)'
caught_variable='([A-Za-z_]*([Ee]x|[Ee]xception|[Ee]rror|[Ee]rr)|e)(\.Message|\.ToString\(\))'
interpolated_whole='\{ *(e|ex|err|error|exception|Ex|Err|Error|Exception)[0-9]* *[}:]'
if grep -rnE "$screen_call\((|[^)]*[^A-Za-z0-9_.])$caught_variable|$screen_call\([^)]*$interpolated_whole" \
        --include="*.cs" coppercli/ coppercli.Core/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -q .; then
    fail no-exception-text-on-screen "an exception message is shown to the operator; use MenuHelpers.ShowFailure or WriteFailure"
fi

# --- rule: one-way-back-to-idle -------------------------------------------
# ReleaseAsync is the only route back to Idle. Reset refuses a controller that still claims
# a run, so no caller writes its own guard.
# Every .Reset(), with the non-controller ones named. Narrowed to receivers spelled
# "controller", it missed a field, a local, and a call through a method.
if grep -rn "\.Reset()" --include="*.cs" coppercli/ coppercli.Core/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -v "ControllerBase\.cs" \
        | grep -vE "(GCodeParser|Stopwatch|[Ss]w|[Tt]imer|[Ee]vent|ManualResetEvent[A-Za-z]*)\.Reset\(\)" \
        | grep -vE '^[^:]*:[0-9]+: *(//|\*|/\*)' | grep -q .; then
    fail one-way-back-to-idle "a controller is Reset directly; call ReleaseAsync, which stops an unfinished run first"
fi

# --- rule: web-ui-needs-no-typed-credential -------------------------------
# No secret the operator would have to carry in a URL or type by hand.
if grep -rniE "accesstoken|\?token=|QueryParamToken|BearerPrefix|coppercli_token" \
        --include="*.cs" --include="*.js" coppercli/WebServer/ 2>/dev/null | grep -q .; then
    fail web-ui-needs-no-typed-credential "a per-run token or bearer credential is present in the web server or client"
fi

# --- rule: workflows-live-in-controllers ----------------------------------
# No UI issues machine commands directly; they funnel through Helpers/MachineCommands.cs.
if grep -rn "SendLine" --include="*.cs" coppercli/Menus/ coppercli/WebServer/ 2>/dev/null | grep -q .; then
    fail workflows-live-in-controllers "a menu or HTTP handler calls SendLine directly instead of MachineCommands"
fi

# --- rule: machine-readiness-is-the-controllers ---------------------------
# The controller answers whether the machine's own state allows a job: it prompts about the
# enclosure, releases the hold, and settles the machine. A gate in front of it refuses a
# machine the controller would have recovered, with a message the operator cannot act on.
#
# Two greps. EnsureMachineReady is the gate helper, which the app layer should not call at
# all. An idle-wait is the same gate written out by hand; that grep covers coppercli/Menus/
# and coppercli/WebServer/ only, because MacroRunner waits for idle to sequence its own
# steps, which is a different question.
if grep -rn "EnsureMachineReady" --include="*.cs" coppercli/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" \
        | grep -vE '^[^:]*:[0-9]+: *(//|\*|/\*)' | grep -q .; then
    fail machine-readiness-is-the-controllers "a UI runs the machine-readiness gate; MillingController owns that question"
fi

# The gate spelled some other way is caught by
# WebServerSequenceTests.ADoorHoldDoesNotBlockTheMill_TheControllerPromptsInstead.
if grep -rnE "(WaitForIdle|WaitForStableIdle)" --include="*.cs" \
        coppercli/Menus/ coppercli/WebServer/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" \
        | grep -vE '^[^:]*:[0-9]+: *(//|\*|/\*)' | grep -q .; then
    fail machine-readiness-is-the-controllers "a menu or handler waits for idle before starting a job; the controller settles the machine"
fi

# --- rule: a-new-distinction-lands-with-its-callers ------------------------
# A question split into more cases is finished only when every place that branched on the old
# question reads the new one. Until then the build passes and the screens still show the
# defect. Matched whatever the return type is, because a grep naming only bool misses one
# returning an enum. It does not match a generic return type, and overloads share a name, so
# one adopted overload covers the others.
#
# Only coppercli/ and coppercli.Core/ are searched, comments are stripped first, and the call
# must be a member access, so a test, a mention in a comment and a same-named private method
# elsewhere all fail to count as adoption.
for answer in $(grep -oE '(public|internal) static (async )?[A-Za-z]+(<[^>]*>)?\??(\[\])? [A-Za-z]+ *\(' \
        coppercli.Core/Controllers/MachineWait.cs 2>/dev/null \
        | sed 's/ *($//; s/ *($//' | awk '{print $NF}' | sed 's/($//' | sort -u); do
    if ! grep -rn "\.$answer *(" --include="*.cs" coppercli/ coppercli.Core/ 2>/dev/null \
            | grep -v "/obj/\|/bin/" \
            | sed 's|//.*$||' | grep "\.$answer *(" \
            | grep -v "^coppercli.Core/Controllers/MachineWait\.cs:" | grep -q .; then
        fail a-new-distinction-lands-with-its-callers "MachineWait.$answer has no caller outside MachineWait; it is unadopted or dead"
    fi
done

# --- rule: culture-invariant-gcode ----------------------------------------
# Numeric G-code formatting must pin the invariant culture, never the ambient one.
if grep -rnE 'ToString\("[Ff][0-9]+"\)|ToString\("0\.0+"\)' --include="*.cs" coppercli.Core/ coppercli/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -q .; then
    fail culture-invariant-gcode "a bare ToString(\"Fn\") can emit a comma decimal separator; use GCodeFormat"
fi

# The idiom here is Inv($"... Z{h:F3}"); dropping the Inv( is how it breaks. Matches a
# coordinate word followed by a formatted number on a line that does not wrap it, wherever
# G-code is built. Screens and log lines format coordinates the same way and are excluded.
# CultureInvariantGCodeTests pins the behaviour.
if grep -rnE '\$"[^"]*[XYZIJKFSR]\{[^}]*:([Ff][0-9]|0\.0)' --include="*.cs" \
        coppercli.Core/ coppercli/Helpers/ coppercli/Menus/ coppercli/Macro/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -v 'Logger\.\|ControllerLog\.' \
        | grep -vE "$screen_call" \
        | grep -v 'Inv(' | grep -q .; then
    fail culture-invariant-gcode "a G-code line formats a coordinate without GCodeFormat.Inv; the decimal separator follows the ambient culture"
fi

# --- rule: a-test-must-be-able-to-fail ------------------------------------
# A test that mutates process-wide state decides whether other classes pass. xUnit runs
# classes in parallel, and it ignores a [Collection] name with no [CollectionDefinition]
# without warning, so the attribute is never evidence of isolation.
if [ -d coppercli.Tests ]; then
    if grep -rn "DefaultThreadCurrentCulture\|DefaultThreadCurrentUICulture" \
            --include="*.cs" coppercli.Tests/ 2>/dev/null \
            | grep -v "/obj/\|/bin/" | grep -vE '^[^:]*:[0-9]+: *(//|\*|/\*)' | grep -q .; then
        fail a-test-must-be-able-to-fail "a test sets a process-wide culture; scope it to the thread (CultureInfo.CurrentCulture)"
    fi

    # Matched whatever is inside the brackets, because this repo names its collection
    # through a constant rather than a literal.
    used=$(grep -rhoE '\[Collection\([^)]+\)\]' --include="*.cs" coppercli.Tests/ 2>/dev/null \
        | sed -E 's/\[Collection\((.*)\)\]/\1/' | sort -u)
    defined=$(grep -rhoE '\[CollectionDefinition\([^)]+\)\]' --include="*.cs" coppercli.Tests/ 2>/dev/null \
        | sed -E 's/\[CollectionDefinition\((.*)\)\]/\1/' | sort -u)
    if [ -n "$used" ]; then
        undefined=$(echo "$used" | grep -vxF "$defined" 2>/dev/null)
        if [ -n "$undefined" ]; then
            fail a-test-must-be-able-to-fail "[Collection] names with no [CollectionDefinition] are silently ignored: $(echo "$undefined" | tr '\n' ' ')"
        fi
    fi
fi

exit $status
