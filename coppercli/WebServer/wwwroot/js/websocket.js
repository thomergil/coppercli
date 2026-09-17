import { state } from './state.js';
import { showError, showInfo, showConfirm, postJson } from './helpers.js';
import { updateStatus, showConnectionStatus } from './screens.js';
import {
    MAX_RECONNECT_ATTEMPTS,
    RECONNECT_DELAY_MS,
    FORCE_DISCONNECT_RECONNECT_DELAY_MS,
    WEBSOCKET_PING_INTERVAL_MS,
    CMD_PING,
    MSG_TYPE_STATUS,
    MSG_TYPE_MILL_STATE,
    MSG_TYPE_MILL_PROGRESS,
    MSG_TYPE_MILL_TOOLCHANGE,
    MSG_TYPE_MILL_ERROR,
    MSG_TYPE_TOOLCHANGE_STATE,
    MSG_TYPE_TOOLCHANGE_PROGRESS,
    MSG_TYPE_TOOLCHANGE_INPUT,
    MSG_TYPE_TOOLCHANGE_COMPLETE,
    MSG_TYPE_TOOLCHANGE_ERROR,
    MSG_TYPE_PROBE_ERROR,
    MSG_TYPE_CONNECTION_ERROR,
    TEXT_CONNECTION_LOST,
    TEXT_FORCE_DISCONNECT_CONFIRM,
    TEXT_FORCE_DISCONNECT_FAILED,
    TITLE_FORCE_DISCONNECT,
    API_FORCE_DISCONNECT,
    WS_CLOSE_REASON_FORCE_DISCONNECT,
    FORCE_DISCONNECT_RELOAD_DELAY_MS,
    WS_PATH,
    WS_QUERY_PARAM_CLIENT_ID,
    CLIENT_ID_COOKIE_NAME,
    TEXT_CONNECTION_ERROR
} from './constants.js';
import { handleMillControllerEvent, handleToolChangeControllerEvent } from './mill.js';
import { checkAndShowTrustZero } from './trust-zero.js';

let pingInterval = null;

function getClientIdFromCookie() {
    const match = document.cookie.match(
        new RegExp(`${CLIENT_ID_COOKIE_NAME}=([^;]+)`));
    return match ? match[1] : null;
}

export function connectWebSocket() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const clientId = getClientIdFromCookie();
    const base = `${protocol}//${window.location.host}${WS_PATH}`;
    const wsUrl = clientId
        ? `${base}?${WS_QUERY_PARAM_CLIENT_ID}=${clientId}`
        : base;

    state.ws = new WebSocket(wsUrl);

    state.ws.onopen = () => {
        console.log('WebSocket connected');
        const isReconnect = state.reconnectAttempts > 0;
        state.reconnectAttempts = 0;
        showConnectionStatus(true);

        if (isReconnect) {
            checkAndShowTrustZero();
        }

        // Without a keep-alive the server drops a socket that goes quiet during a long run.
        if (pingInterval) {
            clearInterval(pingInterval);
        }
        pingInterval = setInterval(() => {
            if (state.ws && state.ws.readyState === WebSocket.OPEN) {
                state.ws.send(JSON.stringify({ type: CMD_PING }));
            }
        }, WEBSOCKET_PING_INTERVAL_MS);
    };

    state.ws.onmessage = (event) => {
        try {
            const msg = JSON.parse(event.data);
            switch (msg.type) {
                case MSG_TYPE_STATUS:
                    updateStatus(msg.data);
                    break;
                case MSG_TYPE_MILL_STATE:
                case MSG_TYPE_MILL_PROGRESS:
                case MSG_TYPE_MILL_TOOLCHANGE:
                case MSG_TYPE_MILL_ERROR:
                    handleMillControllerEvent(msg.type, msg.data);
                    break;
                case MSG_TYPE_TOOLCHANGE_STATE:
                case MSG_TYPE_TOOLCHANGE_PROGRESS:
                case MSG_TYPE_TOOLCHANGE_INPUT:
                case MSG_TYPE_TOOLCHANGE_COMPLETE:
                case MSG_TYPE_TOOLCHANGE_ERROR:
                    handleToolChangeControllerEvent(msg.type, msg.data);
                    break;
                case MSG_TYPE_PROBE_ERROR:
                    // A skipped point is reported as information; only a fatal probe error
                    // is shown as an error.
                    if (msg.data.isFatal === false) {
                        showInfo(msg.data.message);
                    } else {
                        showError(msg.data.message);
                    }
                    break;
                case MSG_TYPE_CONNECTION_ERROR:
                    handleConnectionError(msg.data);
                    break;
            }
        } catch (err) {
            console.error('Failed to parse message:', err);
        }
    };

    state.ws.onclose = (event) => {
        console.log('WebSocket disconnected:', event.code, event.reason, event.wasClean);
        showConnectionStatus(false);

        if (pingInterval) {
            clearInterval(pingInterval);
            pingInterval = null;
        }

        if (state.reconnectAttempts < MAX_RECONNECT_ATTEMPTS) {
            state.reconnectAttempts++;
            // A take-over waits longer than an ordinary drop, so the other client connects first.
            const isForceDisconnect = event.reason === WS_CLOSE_REASON_FORCE_DISCONNECT;
            const delay = isForceDisconnect ? FORCE_DISCONNECT_RECONNECT_DELAY_MS : RECONNECT_DELAY_MS;
            console.log(`Reconnecting (attempt ${state.reconnectAttempts}/${MAX_RECONNECT_ATTEMPTS}) in ${delay}ms...`);
            setTimeout(connectWebSocket, delay);
        } else {
            showError(TEXT_CONNECTION_LOST);
        }
    };

    state.ws.onerror = (err) => {
        console.error('WebSocket error:', err);
    };
}

export function sendCommand(type, data = {}) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN) {
        state.ws.send(JSON.stringify({ type, ...data }));
    }
}

async function handleConnectionError(data) {
    const error = data?.error;

    // Keyed on the otherClientConnected value rather than the message text, which could be
    // reworded without anyone noticing that the take-over stopped being offered.
    if (data?.otherClientConnected === true) {
        if (await showConfirm(TEXT_FORCE_DISCONNECT_CONFIRM, TITLE_FORCE_DISCONNECT)) {
            // This drops the serial port, so a refusal is shown rather than reloaded past.
            const taken = await postJson(API_FORCE_DISCONNECT);
            if (!taken.ok) {
                showError(taken.error || TEXT_FORCE_DISCONNECT_FAILED);
                return;
            }

            setTimeout(() => location.reload(), FORCE_DISCONNECT_RELOAD_DELAY_MS);
        }
    } else {
        showError(error || TEXT_CONNECTION_ERROR);
    }
}
