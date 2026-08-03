// Per-key async mutex + a single-flight helper (C-26).
//
// JavaScript being single-threaded does not make these operations atomic: every one of them is a
// read-modify-write across `await` points. `syncLayerMarkers` reads `store.layerMarkerUids[url]` at
// entry and writes it at exit with network I/O in between, so a recurrence tick overlapping a manual
// download meant the second writer clobbered the first's UID list — leaving markers on the map with
// no record of their UIDs, unremovable by Hide, Delete or Clear All Layers.

type Task<T> = () => Promise<T>;

const chains = new Map<string, Promise<unknown>>();

/**
 * Serializes tasks that share a key. Tasks for different keys still run concurrently, so one slow
 * layer cannot block the others (which a single global lock would do).
 */
export function withLock<T>(key: string, task: Task<T>): Promise<T> {
    const previous = chains.get(key) ?? Promise.resolve();
    // `.catch` (not `.finally`) so one task's rejection does not poison the chain for the next.
    const run = previous.catch(() => undefined).then(task);
    chains.set(key, run.catch(() => undefined));
    void run.catch(() => undefined).then(() => {
        // Drop the entry once this is the tail, so the map does not grow with every layer URL ever
        // touched in a long-running session.
        if (chains.get(key) === undefined) chains.delete(key);
    });
    return run;
}

const inFlight = new Map<string, Promise<unknown>>();

/**
 * Collapses concurrent calls for the same key onto one execution: later callers receive the
 * in-flight promise instead of starting a second run. Used for the OAuth refresh (ArcGIS rotates
 * refresh tokens, so two concurrent refreshes make the second fail and sign the user out) and for
 * `fetchUserLayers`, whose wholesale `splice(0, len, ...merged)` interleaves destructively.
 */
export function singleFlight<T>(key: string, task: Task<T>): Promise<T> {
    const existing = inFlight.get(key) as Promise<T> | undefined;
    if (existing) return existing;
    const run = (async () => {
        try { return await task(); }
        finally { inFlight.delete(key); }
    })();
    inFlight.set(key, run);
    return run;
}

/** True while a `singleFlight` call for `key` is running. Exposed for tests and UI busy states. */
export function isInFlight(key: string): boolean {
    return inFlight.has(key);
}

/** Rejects with `message` if `task` has not settled within `ms` (C-26's per-operation timeout). */
export function withTimeout<T>(ms: number, message: string, task: Task<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(message)), ms);
        task().then(
            (v) => { clearTimeout(timer); resolve(v); },
            (e: unknown) => { clearTimeout(timer); reject(e instanceof Error ? e : new Error(String(e))); },
        );
    });
}
