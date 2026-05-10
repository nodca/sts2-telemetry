namespace Sts2Telemetry;

public sealed class BranchTracker
{
    private readonly Dictionary<string, TrajectoryNode> _firstNodeByCanonicalHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrajectoryNode> _nodesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TrajectoryEdge>> _edgesByParentNodeId = new(StringComparer.Ordinal);
    private int _nextNode;
    private int _nextBranch = 1;
    private int _nextAttempt = 1;

    public string CurrentBranchId { get; private set; } = "branch-0001";
    public string? CurrentParentNodeId { get; private set; }
    public string CurrentBranchStatus { get; private set; } = "active";
    public string CurrentAttemptId { get; private set; } = "attempt-0001";
    public string CurrentAttemptStatus { get; private set; } = "active";
    public string CurrentAttemptSource { get; private set; } = "new_run";
    public int KnownStateCount => _nodesById.Count;

    public TrajectoryNode ObserveState(string canonicalStateHash, string source)
    {
        if (_firstNodeByCanonicalHash.TryGetValue(canonicalStateHash, out var existing))
            return existing;

        var node = CreateNode(canonicalStateHash, CurrentBranchId, CurrentParentNodeId, source);
        CurrentParentNodeId = node.NodeId;
        return node;
    }

    public BranchResumeResult PreviewResume(string canonicalStateHash)
    {
        if (!_firstNodeByCanonicalHash.TryGetValue(canonicalStateHash, out var matched))
        {
            string unknownBranch = _nodesById.Count == 0 ? CurrentBranchId : PreviewNextBranchId();
            return new BranchResumeResult(
                Matched: false,
                PendingDivergence: false,
                BranchId: unknownBranch,
                MatchedNodeId: null,
                ParentCanonicalStateHash: null,
                Reason: "resume_state_not_found");
        }

        bool pendingDivergence = HasKnownEdges(matched.NodeId);
        return new BranchResumeResult(
            Matched: true,
            PendingDivergence: pendingDivergence,
            BranchId: matched.BranchId,
            MatchedNodeId: matched.NodeId,
            ParentCanonicalStateHash: matched.CanonicalStateHash,
            Reason: pendingDivergence
                ? "matched_state_has_existing_children_pending_divergence"
                : "matched_leaf_state");
    }

    public BranchResumeResult ObserveResume(string canonicalStateHash)
    {
        if (!_firstNodeByCanonicalHash.TryGetValue(canonicalStateHash, out var matched))
        {
            StartAttempt("observe_resume", "resume_state_not_found");
            string unknownBranch = _nodesById.Count == 0 ? CurrentBranchId : AllocateBranchId();
            CurrentBranchId = unknownBranch;
            CurrentParentNodeId = null;
            CurrentBranchStatus = "unknown";
            return new BranchResumeResult(
                Matched: false,
                PendingDivergence: false,
                BranchId: unknownBranch,
                MatchedNodeId: null,
                ParentCanonicalStateHash: null,
                Reason: "resume_state_not_found");
        }

        bool pendingDivergence = HasKnownEdges(matched.NodeId);
        StartAttempt(
            "observe_resume",
            pendingDivergence ? "matched_pending_divergence" : "matched_leaf_state");
        CurrentBranchId = matched.BranchId;
        CurrentParentNodeId = matched.NodeId;
        CurrentBranchStatus = pendingDivergence ? "overlap_pending_divergence" : "active";
        return new BranchResumeResult(
            Matched: true,
            PendingDivergence: pendingDivergence,
            BranchId: CurrentBranchId,
            MatchedNodeId: matched.NodeId,
            ParentCanonicalStateHash: matched.CanonicalStateHash,
            Reason: pendingDivergence
                ? "matched_state_has_existing_children_pending_divergence"
                : "matched_leaf_state");
    }

    public BranchDecisionResult RecordDecisionEdge(
        string preCanonicalStateHash,
        string? postCanonicalStateHash,
        string decisionFrameId,
        string? selectedActionCanonicalHash)
    {
        TrajectoryNode preNode = ObserveState(preCanonicalStateHash, "decision_pre_state");
        IReadOnlyList<TrajectoryEdge> knownEdges = KnownEdges(preNode.NodeId);
        TrajectoryEdge? matchedEdge = FindMatchingEdge(
            knownEdges,
            postCanonicalStateHash,
            selectedActionCanonicalHash);

        if (matchedEdge != null)
        {
            TrajectoryNode? matchedChild = null;
            if (matchedEdge.ChildNodeId != null)
                _nodesById.TryGetValue(matchedEdge.ChildNodeId, out matchedChild);

            CurrentBranchId = matchedChild?.BranchId ?? preNode.BranchId;
            CurrentParentNodeId = matchedChild?.NodeId ?? preNode.NodeId;
            CurrentBranchStatus = "active";
            CurrentAttemptStatus = "replayed_known_decision_edge";

            return new BranchDecisionResult(
                Forked: false,
                DivergenceUnknown: false,
                BranchId: CurrentBranchId,
                ParentNodeId: preNode.NodeId,
                ParentCanonicalStateHash: preNode.CanonicalStateHash,
                PostCanonicalStateHash: postCanonicalStateHash,
                SelectedActionCanonicalHash: selectedActionCanonicalHash,
                Reason: "matched_known_decision_edge",
                TrajectoryReplayed: true,
                MatchedDecisionFrameId: matchedEdge.DecisionFrameId,
                MatchedChildNodeId: matchedEdge.ChildNodeId);
        }

        bool hasKnownEdges = knownEdges.Count > 0;
        bool divergenceUnknown = hasKnownEdges
            && !CanProveDivergence(knownEdges, postCanonicalStateHash, selectedActionCanonicalHash);
        bool forked = hasKnownEdges && !divergenceUnknown;

        if (forked)
            CurrentBranchId = AllocateBranchId();

        if (divergenceUnknown)
        {
            CurrentParentNodeId = preNode.NodeId;
            CurrentBranchStatus = "divergence_unknown";
            CurrentAttemptStatus = "divergence_unknown";
            return new BranchDecisionResult(
                Forked: false,
                DivergenceUnknown: true,
                BranchId: CurrentBranchId,
                ParentNodeId: preNode.NodeId,
                ParentCanonicalStateHash: preNode.CanonicalStateHash,
                PostCanonicalStateHash: postCanonicalStateHash,
                SelectedActionCanonicalHash: selectedActionCanonicalHash,
                Reason: "decision_edge_identity_inconclusive");
        }

        CurrentParentNodeId = preNode.NodeId;
        TrajectoryNode? postNode = null;
        if (!string.IsNullOrWhiteSpace(postCanonicalStateHash))
        {
            postNode = forked
                ? CreateNode(postCanonicalStateHash, CurrentBranchId, preNode.NodeId, "decision_post_state")
                : ObserveState(postCanonicalStateHash, "decision_post_state");
            ReplaceNode(postNode with
            {
                BranchId = CurrentBranchId,
                ParentNodeId = preNode.NodeId,
                IncomingDecisionFrameId = decisionFrameId
            });
            CurrentParentNodeId = postNode.NodeId;
        }

        RememberEdge(
            preNode.NodeId,
            new TrajectoryEdge(
                ParentNodeId: preNode.NodeId,
                ChildNodeId: postNode?.NodeId,
                PostCanonicalStateHash: postCanonicalStateHash,
                SelectedActionCanonicalHash: selectedActionCanonicalHash,
                DecisionFrameId: decisionFrameId));
        IncrementChildCount(preNode.NodeId);
        CurrentBranchStatus = "active";
        CurrentAttemptStatus = forked ? "forked" : "active";

        return new BranchDecisionResult(
            Forked: forked,
            DivergenceUnknown: false,
            BranchId: CurrentBranchId,
            ParentNodeId: preNode.NodeId,
            ParentCanonicalStateHash: preNode.CanonicalStateHash,
            PostCanonicalStateHash: postCanonicalStateHash,
            SelectedActionCanonicalHash: selectedActionCanonicalHash,
            Reason: forked ? "diverged_from_known_decision_edges" : "new_child_from_leaf");
    }

    public void MarkCurrentBranchStatus(string status)
    {
        if (!string.IsNullOrWhiteSpace(status))
            CurrentBranchStatus = status;
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata()
    {
        TrajectoryNode? currentNode = null;
        if (CurrentParentNodeId != null)
            _nodesById.TryGetValue(CurrentParentNodeId, out currentNode);

        return new Dictionary<string, object?>
        {
            ["branch_id"] = CurrentBranchId,
            ["branch_status"] = CurrentBranchStatus,
            ["attempt_id"] = CurrentAttemptId,
            ["attempt_status"] = CurrentAttemptStatus,
            ["attempt_source"] = CurrentAttemptSource,
            ["current_state_node_id"] = currentNode?.NodeId,
            ["current_canonical_state_hash"] = currentNode?.CanonicalStateHash,
            ["parent_node_id"] = currentNode?.ParentNodeId,
            ["incoming_decision_frame_id"] = currentNode?.IncomingDecisionFrameId,
            ["known_state_count"] = _nodesById.Count
        };
    }

    private TrajectoryNode CreateNode(
        string canonicalStateHash,
        string branchId,
        string? parentNodeId,
        string source)
    {
        var node = new TrajectoryNode(
            NodeId: $"node-{++_nextNode:000000}",
            CanonicalStateHash: canonicalStateHash,
            BranchId: branchId,
            ParentNodeId: parentNodeId,
            Source: source,
            ChildCount: 0);

        _nodesById[node.NodeId] = node;
        _firstNodeByCanonicalHash.TryAdd(canonicalStateHash, node);
        return node;
    }

    private string AllocateBranchId()
        => $"branch-{++_nextBranch:0000}";

    private string PreviewNextBranchId()
        => $"branch-{_nextBranch + 1:0000}";

    private void StartAttempt(string source, string status)
    {
        CurrentAttemptId = AllocateAttemptId();
        CurrentAttemptSource = source;
        CurrentAttemptStatus = status;
    }

    private string AllocateAttemptId()
        => $"attempt-{++_nextAttempt:0000}";

    private bool HasKnownEdges(string nodeId)
        => _edgesByParentNodeId.TryGetValue(nodeId, out var edges) && edges.Count > 0;

    private IReadOnlyList<TrajectoryEdge> KnownEdges(string nodeId)
        => _edgesByParentNodeId.TryGetValue(nodeId, out var edges)
            ? edges
            : Array.Empty<TrajectoryEdge>();

    private static TrajectoryEdge? FindMatchingEdge(
        IReadOnlyList<TrajectoryEdge> knownEdges,
        string? postCanonicalStateHash,
        string? selectedActionCanonicalHash)
    {
        foreach (TrajectoryEdge edge in knownEdges)
        {
            if (EdgeMatches(edge, postCanonicalStateHash, selectedActionCanonicalHash))
                return edge;
        }

        return null;
    }

    private static bool EdgeMatches(
        TrajectoryEdge edge,
        string? postCanonicalStateHash,
        string? selectedActionCanonicalHash)
    {
        bool compared = false;

        if (!string.IsNullOrWhiteSpace(postCanonicalStateHash)
            && !string.IsNullOrWhiteSpace(edge.PostCanonicalStateHash))
        {
            compared = true;
            if (!string.Equals(edge.PostCanonicalStateHash, postCanonicalStateHash, StringComparison.Ordinal))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(selectedActionCanonicalHash)
            && !string.IsNullOrWhiteSpace(edge.SelectedActionCanonicalHash))
        {
            compared = true;
            if (!string.Equals(edge.SelectedActionCanonicalHash, selectedActionCanonicalHash, StringComparison.Ordinal))
                return false;
        }

        return compared;
    }

    private static bool CanProveDivergence(
        IReadOnlyList<TrajectoryEdge> knownEdges,
        string? postCanonicalStateHash,
        string? selectedActionCanonicalHash)
    {
        if (knownEdges.Count == 0)
            return false;

        return knownEdges.All(edge =>
            (!string.IsNullOrWhiteSpace(postCanonicalStateHash)
                && !string.IsNullOrWhiteSpace(edge.PostCanonicalStateHash))
            || (!string.IsNullOrWhiteSpace(selectedActionCanonicalHash)
                && !string.IsNullOrWhiteSpace(edge.SelectedActionCanonicalHash)));
    }

    private void RememberEdge(string parentNodeId, TrajectoryEdge edge)
    {
        if (!_edgesByParentNodeId.TryGetValue(parentNodeId, out var edges))
        {
            edges = new List<TrajectoryEdge>();
            _edgesByParentNodeId[parentNodeId] = edges;
        }

        if (edges.Any(existing =>
                string.Equals(existing.PostCanonicalStateHash, edge.PostCanonicalStateHash, StringComparison.Ordinal)
                && string.Equals(existing.SelectedActionCanonicalHash, edge.SelectedActionCanonicalHash, StringComparison.Ordinal)))
        {
            return;
        }

        edges.Add(edge);
    }

    private void IncrementChildCount(string nodeId)
    {
        if (!_nodesById.TryGetValue(nodeId, out var node))
            return;

        ReplaceNode(node with { ChildCount = node.ChildCount + 1 });
    }

    private void ReplaceNode(TrajectoryNode node)
    {
        _nodesById[node.NodeId] = node;
        if (!_firstNodeByCanonicalHash.TryGetValue(node.CanonicalStateHash, out var existing)
            || existing.NodeId == node.NodeId)
        {
            _firstNodeByCanonicalHash[node.CanonicalStateHash] = node;
        }
    }
}

public sealed record TrajectoryNode(
    string NodeId,
    string CanonicalStateHash,
    string BranchId,
    string? ParentNodeId,
    string Source,
    int ChildCount,
    string? IncomingDecisionFrameId = null
);

public sealed record TrajectoryEdge(
    string ParentNodeId,
    string? ChildNodeId,
    string? PostCanonicalStateHash,
    string? SelectedActionCanonicalHash,
    string DecisionFrameId
);

public sealed record BranchResumeResult(
    bool Matched,
    bool PendingDivergence,
    string BranchId,
    string? MatchedNodeId,
    string? ParentCanonicalStateHash,
    string Reason
)
{
    public bool Forked => false;
}

public sealed record BranchDecisionResult(
    bool Forked,
    bool DivergenceUnknown,
    string BranchId,
    string ParentNodeId,
    string ParentCanonicalStateHash,
    string? PostCanonicalStateHash,
    string? SelectedActionCanonicalHash,
    string Reason,
    bool TrajectoryReplayed = false,
    string? MatchedDecisionFrameId = null,
    string? MatchedChildNodeId = null
);
