// Engineering Plan - Cytoscape.js interactive graph visualization
(function () {
    'use strict';

    // Register cytoscape-dagre extension if available
    if (typeof cytoscape !== 'undefined' && typeof cytoscapeDagre !== 'undefined') {
        cytoscape.use(cytoscapeDagre);
    }

    let cy = null;
    let dotNetRef = null;

    // Status color palette (matches dashboard theme)
    const STATUS_COLORS = {
        Done:       { bg: '#1a3a2a', border: '#3fb950', text: '#3fb950' },
        InProgress: { bg: '#0d2d3a', border: '#00d4ff', text: '#00d4ff' },
        Assigned:   { bg: '#2a2517', border: '#d29922', text: '#d29922' },
        Pending:    { bg: '#1c2130', border: '#484f58', text: '#8b949e' },
        Blocked:    { bg: '#2a1c1c', border: '#f85149', text: '#f85149' },
        Open:       { bg: '#1a2040', border: '#a371f7', text: '#a371f7' }
    };

    const COMPLEXITY_SIZE = {
        High:   { w: 220, h: 80 },
        Medium: { w: 200, h: 72 },
        Low:    { w: 180, h: 64 }
    };

    window.EngineeringPlan = {
        init: function (containerId, objRef) {
            dotNetRef = objRef;
            if (cy) { cy.destroy(); cy = null; }

            var container = document.getElementById(containerId);
            if (!container) {
                console.error('[EngineeringPlan] Container #' + containerId + ' not found');
                return;
            }
            if (container.offsetWidth === 0 || container.offsetHeight === 0) {
                console.warn('[EngineeringPlan] Container has 0 dimensions, forcing minimum size');
                container.style.minHeight = '400px';
                container.style.minWidth = '400px';
            }

            cy = cytoscape({
                container: container,
                boxSelectionEnabled: false,
                autounselectify: false,
                minZoom: 0.2,
                maxZoom: 3,
                wheelSensitivity: 0.3,
                style: [
                    // Enhancement (parent) nodes
                    {
                        selector: 'node[nodeType="enhancement"]',
                        style: {
                            'shape': 'round-rectangle',
                            'width': 240,
                            'height': 56,
                            'background-color': function (ele) { return (STATUS_COLORS[ele.data('status')] || STATUS_COLORS.Pending).bg; },
                            'border-color': function (ele) { return (STATUS_COLORS[ele.data('status')] || STATUS_COLORS.Pending).border; },
                            'border-width': 2,
                            'border-style': 'dashed',
                            'label': function (ele) { return '📋 #' + ele.data('issueNumber') + ' ' + truncate(ele.data('title'), 28); },
                            'color': function (ele) { return (STATUS_COLORS[ele.data('status')] || STATUS_COLORS.Pending).text; },
                            'font-size': '11px',
                            'font-family': '"Inter", sans-serif',
                            'text-valign': 'center',
                            'text-halign': 'center',
                            'text-wrap': 'wrap',
                            'text-max-width': '220px',
                            'corner-radius': 10,
                            'opacity': 0.85,
                            'z-index': 1
                        }
                    },
                    // Task nodes
                    {
                        selector: 'node[nodeType="task"]',
                        style: {
                            'shape': 'round-rectangle',
                            'width': function (ele) { return (COMPLEXITY_SIZE[ele.data('complexity')] || COMPLEXITY_SIZE.Medium).w; },
                            'height': function (ele) { return (COMPLEXITY_SIZE[ele.data('complexity')] || COMPLEXITY_SIZE.Medium).h; },
                            'background-color': function (ele) { return (STATUS_COLORS[ele.data('status')] || STATUS_COLORS.Pending).bg; },
                            'border-color': function (ele) { return (STATUS_COLORS[ele.data('status')] || STATUS_COLORS.Pending).border; },
                            'border-width': 2,
                            'label': function (ele) {
                                var prefix = ele.data('status') === 'Done' ? '✅' :
                                             ele.data('status') === 'InProgress' ? '🔄' :
                                             ele.data('status') === 'Assigned' ? '👤' : '⏳';
                                return prefix + ' #' + ele.data('issueNumber') + '\n' + truncate(ele.data('title'), 26);
                            },
                            'color': function (ele) { return (STATUS_COLORS[ele.data('status')] || STATUS_COLORS.Pending).text; },
                            'font-size': '11px',
                            'font-family': '"Inter", sans-serif',
                            'text-valign': 'center',
                            'text-halign': 'center',
                            'text-wrap': 'wrap',
                            'text-max-width': function (ele) { return ((COMPLEXITY_SIZE[ele.data('complexity')] || COMPLEXITY_SIZE.Medium).w - 20) + 'px'; },
                            'corner-radius': 8,
                            'z-index': 10
                        }
                    },
                    // Glow for in-progress nodes
                    {
                        selector: 'node[status="InProgress"]',
                        style: {
                            'border-width': 3,
                            'shadow-blur': 15,
                            'shadow-color': '#00d4ff',
                            'shadow-opacity': 0.6,
                            'shadow-offset-x': 0,
                            'shadow-offset-y': 0
                        }
                    },
                    // Selected node highlight
                    {
                        selector: 'node:selected',
                        style: {
                            'border-width': 3,
                            'border-color': '#ffffff',
                            'shadow-blur': 20,
                            'shadow-color': '#ffffff',
                            'shadow-opacity': 0.4
                        }
                    },
                    // Dependency edges (task → task)
                    {
                        selector: 'edge[label="blocks"]',
                        style: {
                            'width': 2,
                            'line-color': '#484f58',
                            'target-arrow-color': '#484f58',
                            'target-arrow-shape': 'triangle',
                            'curve-style': 'bezier',
                            'arrow-scale': 1.2,
                            'opacity': 0.7
                        }
                    },
                    // Parent edges (enhancement → task)
                    {
                        selector: 'edge[label="parent"]',
                        style: {
                            'width': 1.5,
                            'line-color': '#a371f7',
                            'line-style': 'dashed',
                            'target-arrow-color': '#a371f7',
                            'target-arrow-shape': 'triangle-tee',
                            'curve-style': 'bezier',
                            'arrow-scale': 0.8,
                            'opacity': 0.5
                        }
                    },
                    // Highlighted edge (when hovering/selecting a node)
                    {
                        selector: 'edge.highlighted',
                        style: {
                            'width': 3,
                            'line-color': '#00d4ff',
                            'target-arrow-color': '#00d4ff',
                            'opacity': 1,
                            'z-index': 100
                        }
                    },
                    // Faded (non-highlighted) elements
                    {
                        selector: '.faded',
                        style: { 'opacity': 0.15 }
                    }
                ],
                elements: [],
                layout: { name: 'preset' }
            });

            // Event handlers
            cy.on('tap', 'node', function (evt) {
                var data = evt.target.data();
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnNodeClicked', JSON.stringify(data));
                }
            });

            cy.on('mouseover', 'node', function (evt) {
                highlightNeighborhood(evt.target);
            });

            cy.on('mouseout', 'node', function () {
                clearHighlight();
            });

            // Double-click to open GitHub issue
            cy.on('dbltap', 'node', function (evt) {
                var url = evt.target.data('issueUrl');
                if (url) window.open(url, '_blank');
            });
        },

        update: function (nodesJson, edgesJson) {
            if (!cy) {
                console.error('[EngineeringPlan] cy not initialized, cannot update');
                return;
            }

            try {
                var nodes = JSON.parse(nodesJson);
                var edges = JSON.parse(edgesJson);

                // Build a set of valid node IDs for edge validation
                var nodeIds = new Set();
                var elements = [];
                nodes.forEach(function (n) {
                    nodeIds.add(n.id);
                    elements.push({ group: 'nodes', data: n });
                });

                // Only add edges where both source and target exist
                var skippedEdges = 0;
                edges.forEach(function (e) {
                    if (nodeIds.has(e.source) && nodeIds.has(e.target)) {
                        elements.push({ group: 'edges', data: { source: e.source, target: e.target, label: e.label, id: e.source + '->' + e.target } });
                    } else {
                        skippedEdges++;
                    }
                });

                if (skippedEdges > 0) {
                    console.warn('[EngineeringPlan] Skipped ' + skippedEdges + ' edges with missing source/target');
                }
                console.log('[EngineeringPlan] Adding ' + nodes.length + ' nodes, ' + (edges.length - skippedEdges) + ' edges');

                cy.elements().remove();
                cy.add(elements);

                // Try dagre layout, fall back to breadthfirst then grid
                try {
                    cy.layout({
                        name: 'dagre',
                        rankDir: 'TB',
                        nodeSep: 60,
                        rankSep: 80,
                        edgeSep: 30,
                        padding: 40,
                        animate: true,
                        animationDuration: 500,
                        animationEasing: 'ease-out-cubic',
                        fit: true,
                        spacingFactor: 1.1
                    }).run();
                } catch (layoutErr) {
                    console.warn('[EngineeringPlan] Dagre layout failed, falling back to grid:', layoutErr);
                    cy.layout({ name: 'grid', fit: true, padding: 40, animate: true }).run();
                }
            } catch (err) {
                console.error('[EngineeringPlan] Update failed:', err);
            }
        },

        fitGraph: function () {
            if (cy) cy.fit(undefined, 50);
        },

        zoomIn: function () {
            if (cy) cy.zoom({ level: cy.zoom() * 1.3, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
        },

        zoomOut: function () {
            if (cy) cy.zoom({ level: cy.zoom() / 1.3, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
        },

        changeLayout: function (layoutName) {
            if (!cy) return;
            var opts = { animate: true, animationDuration: 500, fit: true, padding: 40 };
            switch (layoutName) {
                case 'dagre':
                    Object.assign(opts, { name: 'dagre', rankDir: 'TB', nodeSep: 60, rankSep: 80, spacingFactor: 1.1 });
                    break;
                case 'breadthfirst':
                    Object.assign(opts, { name: 'breadthfirst', directed: true, spacingFactor: 1.5 });
                    break;
                case 'circle':
                    Object.assign(opts, { name: 'circle', spacingFactor: 1.5 });
                    break;
                case 'grid':
                    Object.assign(opts, { name: 'grid', spacingFactor: 1.5 });
                    break;
                default:
                    Object.assign(opts, { name: 'dagre', rankDir: 'TB', nodeSep: 60, rankSep: 80 });
            }
            cy.layout(opts).run();
        },

        highlightNode: function (nodeId) {
            if (!cy) return;
            var node = cy.getElementById(nodeId);
            if (node.length > 0) highlightNeighborhood(node);
        },

        clearHighlights: function () {
            clearHighlight();
        },

        destroy: function () {
            if (cy) { cy.destroy(); cy = null; }
            dotNetRef = null;
        }
    };

    function highlightNeighborhood(node) {
        if (!cy) return;
        cy.elements().addClass('faded');
        var neighborhood = node.closedNeighborhood();
        // Also include transitive dependencies (all ancestors)
        var incoming = node.predecessors();
        var outgoing = node.successors();
        var connected = neighborhood.union(incoming).union(outgoing);
        connected.removeClass('faded');
        connected.edges().addClass('highlighted');
    }

    function clearHighlight() {
        if (!cy) return;
        cy.elements().removeClass('faded');
        cy.edges().removeClass('highlighted');
    }

    function truncate(str, maxLen) {
        if (!str) return '';
        return str.length > maxLen ? str.substring(0, maxLen - 1) + '…' : str;
    }
})();
