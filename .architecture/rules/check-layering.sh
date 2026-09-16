#!/usr/bin/env sh
# Mechanizable checks for the coppercli architecture contract.
# Run from the repository root:  sh .architecture/rules/check-layering.sh
# Exit 0 = clean, 1 = at least one violation. Each violation prints its rule name.

status=0
fail() { echo "VIOLATION [$1] $2"; status=1; }

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
# Every MSG_TYPE_* declared in constants.js must also be validated in helpers.js.
if [ -f coppercli/WebServer/wwwroot/js/constants.js ] && [ -f coppercli/WebServer/wwwroot/js/helpers.js ]; then
    declared=$(grep -o 'MSG_TYPE_[A-Z_]*' coppercli/WebServer/wwwroot/js/constants.js | sort -u)
    validated=$(grep -o 'MSG_TYPE_[A-Z_]*' coppercli/WebServer/wwwroot/js/helpers.js | sort -u)
    missing=$(echo "$declared" | grep -vxF "$validated" 2>/dev/null)
    if [ -n "$missing" ]; then
        fail ws-message-types-updated-in-four-places "declared in constants.js but not validated in helpers.js: $(echo "$missing" | tr '\n' ' ')"
    fi
fi

# --- rule: one-handler-per-control ----------------------------------------
# onclick and addEventListener are separate slots and both fire, so a control bound through
# each runs its handler twice on one tap.
html=coppercli/WebServer/wwwroot/index.html
jsdir=coppercli/WebServer/wwwroot/js
if [ -f "$html" ] && [ -d "$jsdir" ]; then
    doubled=""
    for id in $(grep -o 'id="[A-Za-z0-9_-]*"' "$html" | sed 's/id="//; s/"$//' | sort -u); do
        listens=$(grep -rn "['\"]$id['\"]" "$jsdir" 2>/dev/null | grep -c "addEventListener")
        [ "$listens" -eq 0 ] && continue

        # An onclick write names the element directly, or through a local holding it.
        clicks=$(grep -rn "['\"]$id['\"]" "$jsdir" 2>/dev/null | grep -c '\.onclick')
        for f in "$jsdir"/*.js; do
            for v in $(sed -n "s/.*[^A-Za-z0-9_]\([A-Za-z_][A-Za-z0-9_]*\) *= *\(document\.getElementById\|\$\)(['\"]$id['\"]).*/\1/p" "$f" 2>/dev/null | sort -u); do
                n=$(grep -c "$v\.onclick" "$f" 2>/dev/null)
                clicks=$((clicks + n))
            done
        done

        [ "$clicks" -gt 0 ] && doubled="$doubled $id"
    done
    if [ -n "$doubled" ]; then
        fail one-handler-per-control "bound by both addEventListener and onclick, so one tap fires twice:$doubled"
    fi
fi

# --- rule: ui-text-is-a-constant ------------------------------------------
# Text a person reads is named once in constants.js, never spelled at the point of use.
if grep -rnE "\.(textContent|innerHTML) *= *['\"][A-Za-z]|show(Error|Info|Confirm)\(['\"][A-Za-z]" \
        --include="*.js" coppercli/WebServer/wwwroot/js/ 2>/dev/null \
        | grep -v constants.js | grep -q .; then
    fail ui-text-is-a-constant "a string a person reads is written at the point of use; name it in constants.js"
fi

# --- rule: no-exception-text-on-screen ------------------------------------
# An exception message names files, offsets and types the operator cannot act on. It goes to
# the log; the screen gets a sentence about what failed. Matches the catch-variable names the
# codebase uses; on the C# side WriteFailure is the one path that answers a caught exception.
if grep -rnE "show(Error|Info|Confirm)\(.*(err|error|e|ex)\.(message|stack)" \
        --include="*.js" coppercli/WebServer/wwwroot/js/ 2>/dev/null | grep -q .; then
    fail no-exception-text-on-screen "an exception message is shown to the operator; log it and show a sentence they can act on"
fi

# --- rule: one-way-back-to-idle -------------------------------------------
# ReleaseAsync is the only route back to Idle. Reset refuses a controller that still claims
# a run, so no caller writes its own guard.
if grep -rn "\.Reset()" --include="*.cs" coppercli/ coppercli.Core/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -v "ControllerBase\.cs" \
        | grep -v "GCodeParser\.Reset()" \
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

# --- rule: culture-invariant-gcode ----------------------------------------
# Numeric G-code formatting must pin the invariant culture, never the ambient one.
if grep -rnE 'ToString\("F[0-9]"\)' --include="*.cs" coppercli.Core/ coppercli/ 2>/dev/null \
        | grep -v "/obj/\|/bin/" | grep -q .; then
    fail culture-invariant-gcode "a bare ToString(\"Fn\") can emit a comma decimal separator; use GCodeFormat"
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

    used=$(grep -rhoE '\[Collection\("[^"]+"\)\]' --include="*.cs" coppercli.Tests/ 2>/dev/null \
        | sed -E 's/.*"(.*)".*/\1/' | sort -u)
    defined=$(grep -rhoE '\[CollectionDefinition\("[^"]+"\)\]' --include="*.cs" coppercli.Tests/ 2>/dev/null \
        | sed -E 's/.*"(.*)".*/\1/' | sort -u)
    if [ -n "$used" ]; then
        undefined=$(echo "$used" | grep -vxF "$defined" 2>/dev/null)
        if [ -n "$undefined" ]; then
            fail a-test-must-be-able-to-fail "[Collection] names with no [CollectionDefinition] are silently ignored: $(echo "$undefined" | tr '\n' ' ')"
        fi
    fi
fi

exit $status
