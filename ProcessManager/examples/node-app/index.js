const MIN_INTERVAL_MS = 200;
const MAX_INTERVAL_MS = 3000;
const ERROR_CHANCE = 0.2;
const BURST_CHANCE = 0.3;
const BURST_COUNT_MIN = 3;
const BURST_COUNT_MAX = 10;

let iteration = 0;

function randomInt(min, max) {
    return Math.floor(Math.random() * (max - min + 1)) + min;
}

function logLine(i, isError = false) {
    const now = new Date();
    const timestamp = `${now.toLocaleString()}.${String(now.getMilliseconds()).padStart(3, '0')}`;
    if (isError) {
        console.error(`[${timestamp}] Iteration ${i} - error occurred`);
    } else {
        console.info(`[${timestamp}] Iteration ${i} - running`);
    }
}

function runLoop() {
    iteration += 1;

    const isBurst = Math.random() < BURST_CHANCE;

    if (isBurst) {
        const burstCount = randomInt(BURST_COUNT_MIN, BURST_COUNT_MAX);
        console.log(`--- Burst of ${burstCount} lines ---`);
        for (let b = 0; b < burstCount; b++) {
            iteration += 1;
            const isError = Math.random() < ERROR_CHANCE;
            logLine(iteration, isError);
        }
    } else {
        logLine(iteration, Math.random() < ERROR_CHANCE);
    }

    const nextDelay = randomInt(MIN_INTERVAL_MS, MAX_INTERVAL_MS);
    setTimeout(runLoop, nextDelay);
}

console.log(`Starting loop (Ctrl+C to stop)...`);
runLoop();