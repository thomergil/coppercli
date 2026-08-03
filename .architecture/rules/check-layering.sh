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

exit $status
