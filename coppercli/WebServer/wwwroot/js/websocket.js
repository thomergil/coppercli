// coppercli Web UI WebSocket

import { state } from './state.js';
import { showError, showConfirm } from './helpers.js';
import { updateStatus, showConnectionStatus } from './screens.js';
import {
    MAX_RECONNECT_ATTEMPTS,
    RECONNECT_DELAY_MS,
    FORCE_DISCONNECT_RECONNECT_DELAY_MS,
    WEBSOCKET_PING_INTERVAL_MS,
    ERROR_OTHER_CLIENT_SUBSTRING,
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
    WS_CLOSE_REASON_FORCE_DISCONNECT
} from './constants.js';
import { handleMillControllerEvent, handleToolChangeControllerEvent } from './mill.js';
import { checkAndShowTrustZero } from './trust-zero.js';

let pingInterval = null;

function getClientIdFromCookie() {
    const match = document.cookie.match(/coppercli_client_id=([^;]+)/);
    return match ? match[1] : null;
}

export function connectWebSocket() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const clientId = getClientIdFromCookie();
    const wsUrl = clientId
        ? `${protocol}//${window.location.host}/ws?clientId=${clientId}`
        : `${protocol}//${window.location.host}/ws`;

    state.ws = new WebSocket(wsUrl);

    state.ws.onopen = () => {
        console.log('WebSocket connected');
        const isReconnect = state.reconnectAttempts > 0;
        state.reconnectAttempts = 0;
        showConnectionStatus(true);

        // On reconnect after server restart, check if we should offer to trust previous work zero
        if (isReconnect) {
            checkAndShowTrustZero();
        }

        // Start keep-alive pings to prevent server timeout during long operations
        if (pingInterval) {
            clearInterval(pingInterval);
        }
        pingInterval = setInterval(() => {
            if (state.ws && state.ws.readyState === WebSocket.OPEN) {
                state.ws.send(JSON.stringify({ type: 'ping' }));
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
                    showError(msg.data.message);
                    break;
                case MSG_TYPE_CONNECTION_ERROR:
                    handleConnectionError(msg.data.error);
                    break;
            }
        } catch (err) {
            console.error('Failed to parse message:', err);
        }
    };

    state.ws.onclose = (event) => {
        console.log('WebSocket disconnected:', event.code, event.reason, event.wasClean);
        showConnectionStatus(false);

        // Stop keep-alive pings
        if (pingInterval) {
            clearInterval(pingInterval);
            pingInterval = null;
        }

        if (state.reconnectAttempts < MAX_RECONNECT_ATTEMPTS) {
            state.reconnectAttempts++;
            // Use longer delay if kicked by another client to let them connect first
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

async function handleConnectionError(error) {
    // Check if this is an "another client connected" error
    const isOtherClientConnected = error && error.includes(ERROR_OTHER_CLIENT_SUBSTRING);

    if (isOtherClientConnected) {
        // Show force-disconnect confirmation using the standard modal
        if (await showConfirm(TEXT_FORCE_DISCONNECT_CONFIRM, TITLE_FORCE_DISCONNECT)) {
            try {
                await fetch(API_FORCE_DISCONNECT, { method: 'POST' });
                setTimeout(() => location.reload(), 500);
            } catch (err) {
                showError(TEXT_FORCE_DISCONNECT_FAILED + ': ' + err.message);
            }
        }
    } else {
        // Generic connection error
        showError(error || 'Connection error');
    }
}
