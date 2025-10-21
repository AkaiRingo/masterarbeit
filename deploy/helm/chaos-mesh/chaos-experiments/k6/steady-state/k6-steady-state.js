import http from 'k6/http';
import { check, sleep } from 'k6';

function parseDuration(durationStr) {
    const match = durationStr.match(/(\d+)([smh])/);
    if (!match) throw new Error(`Invalid duration format: ${durationStr}`);
    const value = parseInt(match[1]);
    const unit = match[2];
    if (unit === 's') return value;
    if (unit === 'm') return value * 60;
    if (unit === 'h') return value * 3600;
    throw new Error(`Unsupported duration unit: ${unit}`);
}

const VUS = parseInt(__ENV.VUS);
const DURATION_STR = __ENV.DURATION;
const DURATION_SECONDS = parseDuration(DURATION_STR);
const RAMP_SECONDS = Math.round(DURATION_SECONDS * 0.05);
const RAMP_DURATION = `${RAMP_SECONDS}s`;
const STEADY_SECONDS = DURATION_SECONDS - RAMP_SECONDS * 2;
const STEADY_DURATION = `${STEADY_SECONDS}s`;

const BASE = __ENV.BASE_URL;
const ENDPOINT = __ENV.ENDPOINT;
const PRODUCTS = __ENV.PRODUCTS.split(',');
const HEADERS = { 'Content-Type': 'application/json' };
const SLEEP = parseInt(__ENV.SLEEP_IN_SECONDS) || 1;

export const options = {
    scenarios: {
        ramp_up: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: RAMP_DURATION, target: VUS },
            ]
        },
        steady_state: {
            executor: 'constant-vus',
            startTime: RAMP_DURATION,
            vus: VUS,
            duration: STEADY_DURATION,
        },
        ramp_down: {
            executor: 'ramping-vus',
            startTime: `${RAMP_SECONDS + STEADY_SECONDS}s`,
            startVUs: VUS,
            stages: [
                { duration: RAMP_DURATION, target: 0 },
            ]
        }
    },
    noVUConnectionReuse: true,
};

export default function () {
    const product = PRODUCTS[Math.floor(Math.random() * PRODUCTS.length)];
    const qty = 1 + Math.floor(Math.random() * 5);
    const payload = JSON.stringify({ product, quantity: qty });

    const res = http.post(`${BASE}${ENDPOINT}`, payload, { headers: HEADERS });

    check(res, { 'status is 201': (r) => r.status === 201 });

    sleep(SLEEP);
}
