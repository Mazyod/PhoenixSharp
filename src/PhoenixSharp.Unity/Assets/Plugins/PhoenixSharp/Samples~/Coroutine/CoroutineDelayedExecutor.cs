using System;
using System.Collections.Generic;
using Phoenix;
using UnityEngine;


namespace DKNetwork {
    
    public sealed class CoroutineDelayedExecutor: IDelayedExecutor {

        private uint id = 0;
        private Dictionary<uint, Coroutine> coroutines = new Dictionary<uint, Coroutine>();


        public DelayedExecution Execute(Action action, TimeSpan delay) {

            if (CoroutineManager.Instance == null) {
                return default; // application quitting
            }

            var id = ++this.id;
            coroutines[id] = CoroutineManager.Instance.PerformAsync(() => {
                action();
                coroutines.Remove(id);
            }, delay);

            return new(id, this);
        }

        public void Cancel(uint id) {

            if (CoroutineManager.Instance == null) {
                return; // application quitting
            }

            if (coroutines.ContainsKey(id)) {
                CoroutineManager.Instance.StopCoroutine(coroutines[id]);
                coroutines.Remove(id);
            }
        }
    }
}

