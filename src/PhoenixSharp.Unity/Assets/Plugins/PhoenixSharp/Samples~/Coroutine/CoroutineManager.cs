using System;
using UnityEngine;
using System.Collections;

/** Simple singleton that we use to attach coroutines
 */
public sealed class CoroutineManager : Singleton<CoroutineManager> {

    private IEnumerator CallAsync(Action action, TimeSpan? delay) {

        if (delay.HasValue) {
            yield return new WaitForSeconds((float)delay.Value.TotalSeconds);
        } else {
            yield return new WaitForEndOfFrame();
        }

        action();
    }

    private IEnumerator CallContinuously(Action action, TimeSpan? interval) {

        while (true) {
            if (interval.HasValue) {
                yield return new WaitForSecondsRealtime((float)interval.Value.TotalSeconds);
            } else {
                yield return new WaitForEndOfFrame();
            }

            action();
        }
    }
        
    // convenient method to quickly schedule stuff for next frame
    public Coroutine PerformAsync(Action action, TimeSpan? delay = null) {
        return StartCoroutine(CallAsync(action, delay));
    }

    public Coroutine RunForever(Action action, TimeSpan? interval = null) {
        return StartCoroutine(CallContinuously(action, interval));
    }
}
