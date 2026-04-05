using System;
using System.Collections.Generic;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

public class JiggleTreeSegment {

    public Transform transform { get; private set; }
    public JiggleTree jiggleTree { get; private set; }
    public JiggleTreeSegment parent { get; private set; }
    private IJiggleParameterProvider jiggleProvider;
    public bool IsDisposed { get; private set; }
    
    private static List<JigglePointParameters> parametersCache;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Initialize() {
        parametersCache = new();
    }

    public void SetParent(JiggleTreeSegment jiggleTree) {
        parent?.SetDirty();
        parent = jiggleTree;
        parent?.SetDirty();
        JigglePhysics.SetGlobalDirty();
    }

    public JiggleTreeSegment(IJiggleParameterProvider jiggleProvider) {
        this.jiggleProvider = jiggleProvider;
        var rig = jiggleProvider.GetJiggleRigData();
        transform = rig.rootBone;
        JigglePhysics.SetGlobalDirty();
    }

    public bool TryGetJiggleRigData(out JiggleRigData jiggleRigData) {
        jiggleRigData = default;
        if (IsDisposed || jiggleProvider == null) {
            return false;
        }

        try {
            if (jiggleProvider is UnityEngine.Object unityObject && unityObject == null) {
                return false;
            }

            jiggleRigData = jiggleProvider.GetJiggleRigData();
            return jiggleRigData.rootBone != null;
        } catch (MissingReferenceException) {
            return false;
        } catch (NullReferenceException) {
            return false;
        }
    }

    private void OnDirty(JiggleTree obj) {
        SetDirty();
    }

    public void UpdateParametersIfNeeded() {
        if (jiggleTree != null && jiggleProvider != null && jiggleProvider.HasAnimatedParameters && TryGetJiggleRigData(out var jiggleRigData)) {
            jiggleRigData.UpdateParameters(jiggleTree, parametersCache);
        }
    }
    
    public void UpdateParameters() {
        if (jiggleTree != null && TryGetJiggleRigData(out var jiggleRigData)) {
            jiggleRigData.UpdateParameters(jiggleTree, parametersCache);
        }
    }
    

    public void RegenerateJiggleTreeIfNeeded() {
        if (!TryGetJiggleRigData(out var jiggleRigData)) {
            return;
        }

        if (jiggleTree == null) {
            jiggleTree = JigglePhysics.CreateJiggleTree(jiggleRigData, jiggleTree);
            jiggleTree.dirtied += OnDirty;
            return;
        }
        if (jiggleTree.dirty) {
            jiggleTree.dirtied -= OnDirty;
            jiggleTree = JigglePhysics.CreateJiggleTree(jiggleRigData, jiggleTree);
            jiggleTree.dirtied += OnDirty;
        }
    }

    public void SetDirty() {
        if (jiggleTree is { dirty: false }) {
            JigglePhysics.ScheduleRemoveJiggleTree(jiggleTree);
            jiggleTree.SetDirty();
        }
        parent?.SetDirty();
        JigglePhysics.SetGlobalDirty();
    }

    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        if (jiggleTree != null) {
            jiggleTree.dirtied -= OnDirty;
        }

        IsDisposed = true;
        jiggleProvider = null;
        parent = null;
        transform = null;
        jiggleTree = null;
    }

}

}
