using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Rendering;

/*
Author : Cyanilux (https://twitter.com/Cyanilux)
Github Repo : https://github.com/Cyanilux/ShaderGraphVariables

Main Feature :
	- Adds Register Variable and Get Variable nodes to Shader Graph, allowing you to link sections of a graph without connection wires.
		- These nodes (technically subgraphs) include a TextField where you can enter a variable name (not case sensitive).
		- They automatically link up with invisible connections/wires/edges.
		- These variables are local to the graph - they won't be shared between other graphs or subgraphs.
	- Supports Vector and Float types.
		- Vector2/3 will be promoted to Vector4. After Get Variable, can Split and re-Combine after if required.
		- If a float is connected the port will automatically change. However note that DynamicVector/DynamicValue slots (used by most math nodes)
		  currently default to the Vector4 port instead. If you require float, put the value through a Float node before connecting.
	- The variable names are serialized using the node's "synonyms" field, which is unused by the graph (only used for nodes in the Add Node menu).
	  If the tool is removed the graph should still load correctly. However it does use a few SubGraphs and if they don't exist you'll need to 
	  remove those nodes, reinstall the tool, or at least include the SubGraphs from the tool in your Assets.

Extra Features : (see ExtraFeatures.cs for more info)
	- Group Colors (Right-click group name)
	- 'Port Swap' Hotkey (Default : S)
	- 'Add Node' Hotkeys (Default : Alpha Number keys, 1 to 0)
		- To change nodes : Tools → SGVariablesExtraFeatures → Rebind Node Bindings

	- To edit keybindings : Edit → Shortcuts (search for SGVariables)
		- Note, try to avoid conflicts with SG's hotkeys (mainly A, F and O) as those can't be rebound
		- https://www.cyanilux.com/tutorials/intro-to-shader-graph/#shortcuts

Setup:
	- Install via Package Manager → Add package via git URL : https://github.com/Cyanilux/ShaderGraphVariables.git
	- Alternatively, download and put the folder in your Assets

Usage : 
	1) Add Node → Register Variable
		- The node has a text field in the place of it's output port where you can type a variable name.
		- Attach a Float/Vector to the input port.
	2) Add Node → Get Variable
		- Again, it has a text field but this time for the input port. Type the same variable name
		- Variable names aren't case sensitive. "Example" would stil link to "EXAMPLE" or "ExAmPlE" etc.
		- When the variable name matches, the in-line input port value (e.g. (0,0,0,0)) should disappear and the preview will change.
		- A connection/edge may blink temporarily, but then is hidden to keep the graph clean.
		- You can now use the output of that node as you would with anything else.

Known Issues :
	- If a 'Get Variable' node is connected to the vertex stage and then a name is entered, it can cause shader errors
		if fragment-only nodes are used by the variable (e.g. cannot map expression to vs_5_0 instruction set)
	- If a node uses a DynamicVector/DynamicValue slot (Most math nodes) it will default to Vector4.
		If you want Float, pass the value through the Float node before connecting!
	- Got an issue, check : https://github.com/Cyanilux/ShaderGraphVariables/issues, if it's not there, add it!
*/

namespace Cyan {

	[InitializeOnLoad]
	public class SGVariables {

		// Debug ----------------------------------------------
		
		internal static bool debugMessages = false;

		private static bool disableTool = false;
		private static bool disableVariableNodes = false;
		private static bool disableExtraFeatures = false;
		private static bool debugPutTextFieldAboveNode = false;
		private static bool debugDontHideEdges = false;

		//	----------------------------------------------------

		// Sorry if the code is badly organised~

		// Sources
		// https://github.com/Unity-Technologies/Graphics/tree/master/com.unity.shadergraph/Editor
		// https://github.com/Unity-Technologies/UnityCsReference/tree/master/Modules/GraphViewEditor

		private static float initTime;
		private static bool isEnabled;
        //private static bool revalidateGraph = false;

        static SGVariables() {
			if (disableTool) return;
			Start();
		}

		public static void Start() {
			if (isEnabled) return;
			initTime = Time.realtimeSinceStartup;
			EditorApplication.update += CheckForGraphs;
			Undo.undoRedoPerformed += OnUndoRedo;
			isEnabled = true;
		}

		public static void Stop() {
			EditorApplication.update -= CheckForGraphs;
			Undo.undoRedoPerformed -= OnUndoRedo;
			isEnabled = false;
		}

		internal static EditorWindow sgWindow;
		internal static EditorWindow prev;
		internal static GraphView graphView;
        internal static bool sgHasFocus;
        internal static bool loadVariables;

        // (Font needed to get string width for variable fields)
        private static Font m_LoadedFont;// = EditorGUIUtility.LoadRequired("Fonts/Inter/Inter-Regular.ttf") as Font;

		private static void CheckForGraphs() {
			if (Time.realtimeSinceStartup < initTime + 3f) return;

			EditorWindow focusedWindow = EditorWindow.focusedWindow;
			if (focusedWindow == null) return;

			if (focusedWindow.GetType().ToString().Contains("ShaderGraph")) {
				// is Shader Graph
                sgHasFocus = true;
                if (focusedWindow != prev || graphView == null) {
					sgWindow = focusedWindow;

					// Focused (new / different) Shader Graph window
					if (debugMessages) Debug.Log("Switched Graph (variables cleared)");

					graphView = GetGraphViewFromMaterialGraphEditWindow(focusedWindow);

					// Clear the stored variables and reload variables
					variables.Clear();
					loadVariables = true;
					prev = focusedWindow;
				}

				if (graphView != null) {
					if (!disableVariableNodes) UpdateVariableNodes();
					if (!disableExtraFeatures) ExtraFeatures.UpdateExtraFeatures();
					loadVariables = false;
					//if (revalidateGraph) ValidateGraph();
                }
			}else{
                sgHasFocus = false;
            }
		}

		#region SGVariables
		private static List<Port> editedPorts = new List<Port>();

		private static void OnUndoRedo() {
			// Undo/Redo redraws the graph, so will cause "key already in use" errors.
			// Doing this will trigger the variables to be reloaded
			prev = null;
			initTime = Time.realtimeSinceStartup;
		}

		private static void UpdateVariableNodes() {
			HandlePortUpdates();

			Action<Node> nodeAction = (Node node) => {
				if (node == null) return;
				if (node.title.Equals("Register Variable")) {
					TextField field = TryGetTextField(node);
					if (field == null) {
						// Register Variable Setup (called once)
						field = CreateTextField(node, out string variableName);
						field.style.marginLeft = 25;
						field.style.marginRight = 4;
						if (!variableName.Equals("")) {
							// Register the node
							Register("", variableName, node);
						}

						field.RegisterValueChangedCallback(x => Register(x.previousValue, x.newValue, node));

						// Setup Node Type (Vector/Float)
						var inputPorts = GetInputPorts(node);
						Port inputVector = inputPorts.AtIndex(0);
						Port inputFloat = inputPorts.AtIndex(1);
						Port connectedOutput = GetConnectedPort(inputVector);
						Port connectedOutputF = GetConnectedPort(inputFloat);
						NodePortType portType = NodePortType.Vector4;

						if (connectedOutput != null) {
							if (GetPortType(connectedOutput).Contains("Vector1")) {
								portType = NodePortType.Float;
							}
						}
						if (connectedOutputF != null) {
							if (GetPortType(connectedOutputF).Contains("Vector1")) {
								portType = NodePortType.Float;
							}
						}

						SetNodePortType(node, portType);

						if (connectedOutput == null && connectedOutputF == null) {
						} else if (connectedOutput == null || connectedOutputF == null) {
							// Only one of the ports is connected.
							// This can happen if node was created while dragging an edge from an output port
							// We need to make sure both are connected :
							if (connectedOutput == null) {
								Connect(connectedOutputF, inputVector);
								Connect(connectedOutputF, inputFloat);
							} else {
								Connect(connectedOutput, inputVector);
								Connect(connectedOutput, inputFloat);
							}
						}

						var outputPorts = GetOutputPorts(node);
						Port outputVector = outputPorts.AtIndex(0);
						Port outputFloat = outputPorts.AtIndex(1);
						Port connectedInput = GetConnectedPort(outputVector);
						Port connectedInputF = GetConnectedPort(outputFloat);
						if ((connectedInput != null && !connectedInput.node.title.Equals("Get Variable")) ||
							(connectedInputF != null && !connectedInputF.node.title.Equals("Get Variable"))) {
							// Not allowed to connect to the inputs of Register Variable node
							// (unless it's the Get Variable node, which is connected automatically)
							// This can happen if node was created while dragging an edge from an input port
							DisconnectAllOutputs(node);
						}

						// Register methods to port.OnConnect / port.OnDisconnect, (is internal so we use reflection)
						inputPorts.ForEach((Port port) => {
							RegisterPortDelegates(port, OnRegisterNodeInputPortConnected, OnRegisterNodeInputPortDisconnected);
						});
						// If this breaks, an alternative is to just check the ports each frame for different types

					} else {
						// Register Variable Update
						if (loadVariables) {
							Register("", field.value, node);
						}

						var inputPorts = GetInputPorts(node);
						var outputPorts = GetOutputPorts(node);
						Port inputPort = GetActivePort(inputPorts);

						// Make edges invisible
						Action<Port> portAction = (Port output) => {
							foreach (var edge in output.connections) {
								if (edge.input.node.title.Equals("Get Variable")) {
									if (edge.visible && !debugDontHideEdges) edge.visible = false;
								}
							}
						};
						outputPorts.ForEach(portAction);

						// Make edges invisible (if not active input port)
						portAction = (Port input) => {
							foreach (var edge in input.connections) {
								if (edge.input != inputPort) {
									if (edge.visible && !debugDontHideEdges) edge.visible = false;
								}
							}
						};
						inputPorts.ForEach(portAction);

						if (!node.expanded) {
							bool hasPorts = node.RefreshPorts();
							if (!hasPorts) HideElement(field);
						} else {
							ShowElement(field);
						}
					}
				} else if (node.title.Equals("Get Variable")) {
					TextField field = TryGetTextField(node);
					if (field == null) {
						// Get Variable Setup (called once)
						field = CreateTextField(node, out string variableName);
						field.style.marginLeft = 4;
						field.style.marginRight = 25;
						field.RegisterValueChangedCallback(x => Get(x.newValue, node));

						var outputPorts = GetOutputPorts(node);
						Port outputVector = outputPorts.AtIndex(0);
						Port outputFloat = outputPorts.AtIndex(1);

						// If both output ports are visible, do setup :
						// (as Register Variable node may trigger it first)
						if (!IsPortHidden(outputVector) && !IsPortHidden(outputFloat)) {
							string key = GetSerializedVariableKey(node);
							Get(key, node);
						}else if (loadVariables){
							string key = GetSerializedVariableKey(node);
							ResizeNodeToFitText(node, key);
						}

						Port connectedInputF = GetConnectedPort(outputFloat);
						NodePortType portType = GetNodePortType(node);
						if (connectedInputF != null && portType == NodePortType.Vector4) {
							// Something is connected to the Float port, when the type is Vector
							// This can happen if node was created while dragging an edge from an input port
							MoveAllOutputs(node, outputFloat, outputVector);
						}

					} else {
						if (!node.expanded) {
							bool hasPorts = node.RefreshPorts();
							if (!hasPorts) HideElement(field);
						} else {
							ShowElement(field);
						}
						/*
						// Get Variable Update
						var inputPorts = GetInputPorts(node);
						Port inputActive = GetActivePort(inputPorts);

						bool shouldRemove = false;
						string key = GetSerializedVariableKey(node);
						if (variables.ContainsKey(key.ToUpper())){
							foreach (var edge in inputActive.connections) {
								if (!edge.output.node.title.Equals("Register Variable")) {
									shouldRemove = true;
								}
							}
							if (shouldRemove) {
								DisconnectAllEdges(node, inputActive);
								Get(key, node);
							}
						}
						// Not needed anymore, figured out a way to hide the ports and prevent connecting
						*/
					}
				}
			};

            graphView.nodes.ForEach(nodeAction);
        }

		private static void HandlePortUpdates() {
			for (int i = editedPorts.Count - 1; i >= 0; i--) {
				Port port = editedPorts[i];
				Node node = port.node;
				if (node == null) {
					// Node has been deleted, ignore
					editedPorts.RemoveAt(i);
					continue;
				}

				Port outputConnectedToInput = GetConnectedPort(port);
				if (outputConnectedToInput != null) {
					if (debugMessages) Debug.Log(outputConnectedToInput.portName + " > " + port.portName);

					var inputPorts = GetInputPorts(node);
					Port inputVector = inputPorts.AtIndex(0);
					Port inputFloat = inputPorts.AtIndex(1);

					// Disconnect Inputs & Reconnect
					if (IsPortHidden(inputVector)) {
						DisconnectAllEdges(node, inputVector);
						Connect(outputConnectedToInput, inputVector);
					} else { //if (IsPortHidden(inputFloat)){
						DisconnectAllEdges(node, inputFloat);
						Connect(outputConnectedToInput, inputFloat);
					}
					// we avoid changing the active port to prevent infinite loop

					string connectedSlotType = GetPortType(outputConnectedToInput);
					string inputSlotType = GetPortType(port);

					if (connectedSlotType != inputSlotType) {
						if (debugMessages) Debug.Log(connectedSlotType + " > " + inputSlotType);

						NodePortType portType = NodePortType.Vector4;
						if (connectedSlotType.Contains("Vector1")) {
							portType = NodePortType.Float;
						}
						/*
						else if (connectedSlotType.Contains("DynamicVector") || connectedSlotType.Contains("DynamicValue")){
							// Handles output slots that can change between vector types (or vector/matrix types)
							// e.g. Most math based nodes use DynamicVector. Multiply uses DynamicValue
							var materialSlot = GetMaterialSlot(outputConnectedToInput);
							FieldInfo dynamicTypeField = materialSlot.GetType().GetField("m_ConcreteValueType", bindingFlags);
							string typeString = dynamicTypeField.GetValue(materialSlot).ToString();
							if (typeString.Equals("Vector1")){
								portType = NodePortType.Vector1;
							}else{
								portType = NodePortType.Vector4;
							}
						}*/
						/*
						- While this works, it introduces a problem where, if we connect a Dynamic port the type changes correctly,
							but then if we trigger the Dynamic port to change type, it doesn't trigger the port Connect/Disconnect
							so the type doesn't change! It does switch type when the graph is reloaded but also kinda bugged.
						- I may be able to fix this further by registering the inputs on the previous node (outputConnectedToInput.node)
							to the Connect/Disconnect events to listen if the type changes...
						- But it's also easier just defaulting to Vector4 and if the user really wants Float, pass it through a Float node first!
						*/

						//Debug.Log("TYPE : " + portType);

						if (inputSlotType.Contains("Vector4") && portType == NodePortType.Float) {
							// If port is currently Vector4, but a Float has been attached
							SetNodePortType(node, NodePortType.Float);
						} else if (inputSlotType.Contains("Vector1") && portType == NodePortType.Vector4) {
							// If port is currently Float, but a Vector2/3/4 has been attached
							SetNodePortType(node, NodePortType.Vector4);
						}
					}
				} else {
					// Removed Port
					var inputPorts = GetInputPorts(node);
					inputPorts.ForEach((Port p) => {
						if (p != port) {
							// we avoid changing the active port to prevent infinite loop
							DisconnectAllEdges(node, p);
						}
					});

					// Default to Vector4 type
					SetNodePortType(node, NodePortType.Vector4);
				}
				editedPorts.RemoveAt(i);
			}
		}
		#endregion

		#region UIElements
		private static TextField TryGetTextField(Node node) {
			return node.ElementAt(1) as TextField;
		}

		private static TextField CreateTextField(Node node, out string variableName) {
			// Get Variable Name (saved in the node's "synonyms" field)
			variableName = GetSerializedVariableKey(node);

			// Setup Text Field 
			TextField field = new TextField();
			field.style.position = Position.Absolute;
			if (debugPutTextFieldAboveNode) {
				field.style.top = -35; // put field above (debug)
			} else {
				field.style.top = 39; // put field over first input/output port
			}
			field.StretchToParentWidth();
            // Note : Later we also adjust margins so it doesn't hide the required ports

            var textInput = field.ElementAt(0); // TextField -> TextInput
#if UNITY_2022_1_OR_NEWER
textInput = textInput.ElementAt(0); // TextInput -> TextElement
#endif
			textInput.style.fontSize = 25;
			textInput.style.unityTextAlign = TextAnchor.MiddleCenter;
			//textInput.style.borderTopColor = new Color(0.13f, 0.13f, 0.13f);

			field.value = variableName;

			// Add TextField to node VisualElement
			// Note : This must match what's in TryGetTextField
			node.Insert(1, field);

			return field;
		}

		private static void ResizeNodeToFitText(Node node, string s) {
			if (m_LoadedFont == null) m_LoadedFont = EditorGUIUtility.LoadRequired("Fonts/Inter/Inter-Regular.ttf") as Font;
			if (m_LoadedFont == null){
				//Debug.LogError("Seems font (Fonts/Inter/Inter-Regular.ttf) is null? Cannot get string width, defaulting to 250");
				node.style.minWidth = 250;
			}else{
				m_LoadedFont.RequestCharactersInTexture(s);
				float width = 0;
				foreach (char c in s) {
					if (m_LoadedFont.GetCharacterInfo(c, out CharacterInfo info)) {
						width += info.glyphWidth + info.advance;
					}
				}
				node.style.minWidth = width + 42; // margins/padding
			}
			node.MarkDirtyRepaint();
			//Debug.Log("ResizeNodeToFitText : " + width + ", string : " + s);
		}

		private static bool IsPortHidden(Port port) {
			return (port.style.display == DisplayStyle.None || port.parent.style.display == DisplayStyle.None);
		}

		private static void HideInputPort(Port port) {
			// The SubGraph input ports have an additional element grouped for when the input is empty, that shows the (0,0,0,0) thing
			HideElement(port.parent);
		}

		private static void HideOutputPort(Port port) {
			HideElement(port);
		}

		private static void ShowInputPort(Port port) {
			ShowElement(port.parent);
		}

		private static void ShowOutputPort(Port port) {
			ShowElement(port);
		}

		private static void HideElement(VisualElement visualElement) {
			visualElement.style.display = DisplayStyle.None;
		}

		private static void ShowElement(VisualElement visualElement) {
			visualElement.style.display = DisplayStyle.Flex;
		}
		#endregion

		#region Get Input/Output Ports
		// Register/Get Variable nodes support these types, should match port order
		private enum NodePortType {
			Vector4, // also used for Vector2, Vector3 and DynamicVector, DynamicValue
			Float
		}

		public static UQueryState<Port> GetInputPorts(Node node) {
			// As a small optimisation (hopefully), we're storing the UQueryState<Port> in userData
			// (maybe a List<Node> would be better?)
			object userData = node.inputContainer.userData;
			if (userData == null) {
				if (debugMessages) Debug.Log("Setup Input Ports, " + node.title + " / " + GetSerializedVariableKey(node));
				UQueryState<Port> inputPorts = node.inputContainer.Query<Port>().Build();
				node.inputContainer.userData = inputPorts;
				inputPorts.ForEach((Port port) => {
					port.userData = GetMaterialSlotTypeReflection(port);
				});
				return inputPorts;
			}
			return (UQueryState<Port>)userData;
		}

		public static UQueryState<Port> GetOutputPorts(Node node) {
			// As a small optimisation (hopefully), we're storing the UQueryState<Port> in userData
			// (maybe a List<Node> would be better?)
			object userData = node.outputContainer.userData;
			if (userData == null) {
				if (debugMessages) Debug.Log("Setup Output Ports, " + node.title + " / " + GetSerializedVariableKey(node));
				UQueryState<Port> outputPorts = node.outputContainer.Query<Port>().Build();
				node.outputContainer.userData = outputPorts;
				outputPorts.ForEach((Port port) => {
					port.userData = GetMaterialSlotTypeReflection(port);
				});
				return outputPorts;
			}
			return (UQueryState<Port>)userData;
		}

		private static Port GetActivePort(UQueryState<Port> ports) {
			List<Port> portsList = ports.ToList();
			foreach (Port p in portsList) {
				if (!IsPortHidden(p)) {
					return p;
				}
			}
			return null;
		}

		/// <summary>
		/// Get the port connected to this port.
		/// If an input port is passed in, there should only be one connected (or zero, in which case this returns null).
		/// If an output port is passed in, the other port in the first connection is returned (or again, null if no connections).
		/// If you need to check every connection for the output port, use "foreach (Edge edge in port.connections){...}" instead.
		/// </summary>
		public static Port GetConnectedPort(Port port) {
			foreach (Edge edge in port.connections) {
				if (edge.parent == null) {
					// ignore any "broken" edges (shouldn't happen anymore (see Connect function), but just to be safe)
					continue;
				}
				Port input = edge.input;
				Port output = edge.output;
				return (output == port) ? input : output;
			}
			return null;
		}

		/// <summary>
		/// Returs a string of the Type of SG MaterialSlot the port uses (e.g. "UnityEditor.ShaderGraph.Vector1MaterialSlot")
		/// </summary>
		public static string GetPortType(Port port) {
			string type = (string)port.userData;
			if (type == null) {
				// Cache in userData so next time if the port is used we don't need to bother obtaining it again
				// (though note this will reset if an undo occurs)
				type = GetMaterialSlotTypeReflection(port);
				port.userData = type;
			}
			return type;
		}

		private static NodePortType GetNodePortType(Node node) {
			bool isRegisterNode = (node.title.Equals("Register Variable"));

			var inputPorts = GetInputPorts(node);
			var outputPorts = GetOutputPorts(node);

			NodePortType currentPortType = NodePortType.Vector4;

			// Hide Inputs
			Port inputVector = inputPorts.AtIndex(0);
			Port inputFloat = inputPorts.AtIndex(1);
			Port outputVector = outputPorts.AtIndex(0);
			Port outputFloat = outputPorts.AtIndex(1);

			if (isRegisterNode) {
				if (!IsPortHidden(inputVector)) {
					currentPortType = NodePortType.Vector4;
				} else if (!IsPortHidden(inputFloat)) {
					currentPortType = NodePortType.Float;
				}
			} else {
				if (!IsPortHidden(outputVector)) {
					currentPortType = NodePortType.Vector4;
				} else if (!IsPortHidden(outputFloat)) {
					currentPortType = NodePortType.Float;
				}
			}
			return currentPortType;
		}

		private static void SetNodePortType(Node node, NodePortType portType) {
			bool isRegisterNode = (node.title.Equals("Register Variable"));

			var inputPorts = GetInputPorts(node);
			var outputPorts = GetOutputPorts(node);

			NodePortType currentPortType = GetNodePortType(node);
			bool typeChanged = (currentPortType != portType);

			Port inputVector = inputPorts.AtIndex(0);
			Port inputFloat = inputPorts.AtIndex(1);
			Port outputVector = outputPorts.AtIndex(0);
			Port outputFloat = outputPorts.AtIndex(1);

			// Hide Ports
			HideInputPort(inputVector);
			HideInputPort(inputFloat);
			HideOutputPort(outputVector);
			HideOutputPort(outputFloat);

			// Show Ports (if Get Variable node and typeChanged, move outputs to "active" port)
			if (portType == NodePortType.Vector4) {
				if (isRegisterNode) {
					ShowInputPort(inputVector);
				} else {
					ShowOutputPort(outputVector);
					if (typeChanged) MoveAllOutputs(node, outputFloat, outputVector);
				}
			} else if (portType == NodePortType.Float) {
				if (isRegisterNode) {
					ShowInputPort(inputFloat);
				} else {
					ShowOutputPort(outputFloat);
					if (typeChanged) MoveAllOutputs(node, outputVector, outputFloat);
				}
			}

			if (isRegisterNode && typeChanged) {
				// Relink to Get Variable nodes
				List<Node> nodes = LinkToAllGetVariableNodes(GetSerializedVariableKey(node).ToUpper(), node);
				foreach (Node n in nodes) {
					SetNodePortType(n, portType);
				}
			}
		}

		private static void OnRegisterNodeInputPortConnected(Port port) {
			if (IsPortHidden(port)) return; // If hidden, ignore connections (also used to prevent infinite loop)

			if (debugMessages) Debug.Log("OnRegisterNodeInputPort Connected (" + port.portName + ")");

			// Sadly it seems we can't edit connections directly here,
			// It errors as a collection is modified while SG is looping over it.
			// To avoid this, we'll assign the port to a list and check it during the EditorApplication.update
			// Note however this delayed action causes the edge to glitch out a bit.
			editedPorts.Add(port);
		}

		private static void OnRegisterNodeInputPortDisconnected(Port port) {
			if (IsPortHidden(port)) return; // If hidden, ignore connections (also used to prevent infinite loop)
			if (debugMessages) Debug.Log("OnRegisterNodeInputPort Disconnected (" + port.portName + ")");

			//DisconnectAllInputs(port.node);
			editedPorts.Add(port);
		}
		#endregion

		#region Register/Get Variables
		private static Dictionary<string, Node> variables = new Dictionary<string, Node>();

		/// <summary>
		/// Adds the (newValue, node) to variables dictionary. Removes previousValue, if editing the correct node.
		/// </summary>
		private static void Register(string previousValue, string newValue, Node node) {
			ResizeNodeToFitText(node, newValue);

			previousValue = previousValue.Trim().ToUpper();
			newValue = newValue.Trim();
			string key = newValue.ToUpper();

			bool previousKey = !previousValue.Equals("");
			bool newKey = !key.Equals("");

			// Remove previous key from Dictionary (if it's the correct node as stored)
			Node n;
			if (previousKey) {
				if (variables.TryGetValue(previousValue, out n)) {
					if (n == node) {
						if (debugMessages) Debug.Log("Removed " + previousValue);
						variables.Remove(previousValue);
					} else {
						if (debugMessages) Debug.Log("Not same node, not removing key");
					}
				}
			}

			if (variables.TryGetValue(key, out n)) {
				// Already contains key, was is the same node? (Changing case, e.g. "a" to "A" triggers this still)
				if (node == n) {
					// Same node. Serialise the new value and return,
					SetSerializedVariableKey(node, newValue);
					return;
				}

				if (n == null || n.userData == null) {
					// Occurs if previous Register Variable node was deleted
					if (debugMessages) Debug.Log("Replaced Null");
					variables.Remove(key);
				} else {
					if (debugMessages) Debug.Log("Attempted to Register " + key + " but it's already in use!");

					ShowValidationError(node, "Variable Key is already in use!");

					SetSerializedVariableKey(node, "");
					return;
				}
			} else {
				ClearErrorsForNode(node);
			}

			// Add new key to Dictionary
			if (newKey) {
				if (debugMessages) Debug.Log("Register " + key);
				variables.Add(key, node);
			}

			// Allow key to be serialised (as user typed, not upper-case version)
			SetSerializedVariableKey(node, newValue);

			var outputPorts = GetOutputPorts(node);
			if (previousKey) {
				// As the value has changed, disconnect any output edges
				// But first, change Get Variable node types back to Vector4 default
				Port outputPort = outputPorts.AtIndex(0); // (doesn't matter which port we use, as all should be connected)
				foreach (Edge edge in outputPort.connections) {
					if (edge.input != null && edge.input.node != null) {
						SetNodePortType(edge.input.node, NodePortType.Vector4);
					}
				}
				DisconnectAllOutputs(node);
			}

			// Check if any 'Get Variable' nodes are using the key and connect them
			if (newKey) {
				NodePortType portType = GetNodePortType(node);
				List<Node> nodes = LinkToAllGetVariableNodes(key, node);
				foreach (Node n2 in nodes) {
					SetNodePortType(n2, portType); // outputPort
				}
			}
		}

		/// <summary>
		/// Used on a Register Variable node to link it to all Get Variable nodes in the graph.
		/// </summary>
		private static List<Node> LinkToAllGetVariableNodes(string key, Node registerNode) {
			if (debugMessages) Debug.Log("LinkToAllGetVariableNodes(" + key + ")");

			List<Node> linkedNodes = new List<Node>();
			Action<Node> nodeAction = (Node n) => {
				if (n.title.Equals("Get Variable")) {
					string key2 = GetSerializedVariableKey(n).ToUpper();
					if (key == key2) {
						LinkRegisterToGetVariableNode(registerNode, n);
						linkedNodes.Add(n);
					}
				}
			};

			graphView.nodes.ForEach(nodeAction);
            //revalidateGraph = true;
            return linkedNodes;
		}

		/// <summary>
		/// Links each output port on the Register Variable node to the input on the Get Variable node (does not ValidateGraph, call manually)
		/// </summary>
		private static void LinkRegisterToGetVariableNode(Node registerNode, Node getNode) {
			//if (debugMessages) Debug.Log("Linked Register -> Get");
			var outputPorts = GetOutputPorts(registerNode);
			var inputPorts = GetInputPorts(getNode);
			int portCount = 2;
			// If ports change this needs updating.
			// This assumes the SubGraphs always have the same number of input/output ports,
			// and the order on both nodes is matching types to allow connections
			for (int i = 0; i < portCount; i++) {
				Port outputPort = outputPorts.AtIndex(i);
				Port inputPort = inputPorts.AtIndex(i);
				Connect(outputPort, inputPort, true);
			}
		}

		/// <summary>
		/// Gets the variable from the variables dictionary and links the Get Variable node to the stored Register Variable node.
		/// </summary>
		private static void Get(string key, Node node) {
			ResizeNodeToFitText(node, key);
			key = key.Trim();

			// Allow key to be serialised
			SetSerializedVariableKey(node, key);

			// Dictionary always uses upper-case version of key
			key = key.ToUpper();

			if (debugMessages) Debug.Log("Get " + key);

			if (variables.TryGetValue(key, out Node varNode)) {
				var outputPorts = GetOutputPorts(varNode);
				var inputPorts = GetInputPorts(node);

				// Make sure Get Variable node matches Register Variable type
				SetNodePortType(node, GetNodePortType(varNode));

				// Link, Register Variable > Get Variable
				DisconnectAllInputs(node);
				LinkRegisterToGetVariableNode(varNode, node);
				//revalidateGraph = true;
			} else {
				// Key doesn't exist. If any inputs, disconnect them
				DisconnectAllInputs(node);

				// Default to Vector4 input
				SetNodePortType(node, NodePortType.Vector4);
			}
		}
		#endregion

		#region Connect/Disconnect Edges
		/// <summary>
		/// Connects two ports with an edge
		/// </summary>
		public static Edge Connect(Port a, Port b, bool noValidate = false) {
			foreach (Edge bEdge in b.connections) {
				foreach (Edge aEdge in a.connections) {
					if (aEdge == bEdge) {
						// Nodes are already connected!
						return aEdge;
					}
				}
			}

			// This connects the ports *visually*, but SG seems to handle this later
			// so using this ends up creating a duplicate edge which we don't want
			//Edge edge = a.ConnectTo(b);

			// But the Reflection method needs an edge passed in, so we'll just create a dummy one I guess?
			Edge edge = new Edge() {
				output = a,
				input = b
			};

			// This connects the ports in terms of the Shader Graph Data
			object sgEdge = ConnectReflection(edge, noValidate);
			if (sgEdge == null) {
				// Oh no, something went wrong!
				if (debugMessages) Debug.LogWarning("sgEdge was null! (This is bad as it'll break copying)");
				// This can cause an error here when trying to copy the node :
				// https://github.com/Unity-Technologies/Graphics/blob/3f3263397f0c880135b4f42d623f1510a153e20e/com.unity.shadergraph/Editor/Util/CopyPasteGraph.cs#L149

				ShowValidationError(edge.input.node, "Failed to Get Variable! Did you create a loop?");
				// Preview may also be incorrect if Register Variable node is float type here
			}
			return edge;
		}

		/// <summary>
		/// Disconnects all edges from the specified node and port
		/// </summary>
		public static void DisconnectAllEdges(Node node, Port port) {
			// If already has no connections, don't bother continuing
			int n = 0;
			foreach (Edge edge in port.connections) {
				n++;
			}
			if (n == 0) return;

			/*
			foreach (Edge edge in port.connections) {
				if (port.direction == Direction.Input) {
					edge.output.Disconnect(edge);
				} else {
					edge.input.Disconnect(edge);
				}
			}
			// Unsure if I really need to disconnect the other end
			// Currently doesn't seem to matter that much so leaving it out
			// When we disconnect below in the reflection, SG triggers ValidateGraph
			// which triggers CleanupGraph and removes "orphan" edges anyway
			*/

			// This disconnects all connections in port *visually*
			port.DisconnectAll();

			int index;
			if (port.direction == Direction.Input) {
				// The SubGraph input ports have an additional element grouped
				// (for showing value when not connected, e.g. the (0,0,0,0) thing)
				VisualElement parent = port.parent;
				index = parent.parent.IndexOf(parent);
			} else {
				index = port.parent.IndexOf(port);
			}

			// This disconnects all connections in port in terms of the Shader Graph Data
			DisconnectAllReflection(node, index, port.direction);
		}

		/// <summary>
		/// Disconnects all edges in all input ports on node
		/// </summary>
		public static void DisconnectAllInputs(Node node) {
			var inputPorts = GetInputPorts(node);
			inputPorts.ForEach((Port port) => {
				DisconnectAllEdges(node, port);
			});
		}

		/// <summary>
		/// Disconnects all edges in all output ports on node
		/// </summary>
		public static void DisconnectAllOutputs(Node node) {
			var outputPorts = GetOutputPorts(node);
			outputPorts.ForEach((Port port) => {
				DisconnectAllEdges(node, port);
			});
		}

		/// <summary>
		/// Moves all outputs on node to a different port on the same node (though might work if toPort is on a different node too?)
		/// </summary>
		public static void MoveAllOutputs(Node node, Port port, Port toPort) {
			// Move all connections from port to toPort (on same node)
			List<Port> toConnect = new List<Port>();
			foreach (Edge edge in port.connections) {
				Port input = edge.input;
				toConnect.Add(input);
			}
			//DisconnectAllEdges(node, port); // Remove all edges from previous port
			// Seems we don't need to disconnect the edges, probably because we're connecting
			// to the same inputs which can only have 1 edge, so it gets overridden

			for (int i = 0; i < toConnect.Count; i++) {
				Connect(toPort, toConnect[i], true);
			}
			//revalidateGraph = true;
		}
		#endregion

		#region Reflection
		// This probably isn't pretty.

		public static object graphData;

		internal const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

		internal static Type graphDataType;
		internal static Type abstractMaterialNodeType;
		internal static Type colorNodeType;

		private static Type materialGraphEditWindowType;
		private static Type graphEditorViewType;
		private static Type materialSlotType;
		private static Type IEdgeType;
		private static Type listType_MaterialSlot;
		private static Type listType_GenericParam0;
		private static Type listType_IEdge;

		internal static FieldInfo synonymsField;

		private static FieldInfo graphEditorViewField;
		private static FieldInfo graphViewField;
		private static FieldInfo graphDataField;
		private static FieldInfo onConnectField;
		private static FieldInfo onDisconnectField;

		private static PropertyInfo shaderPortSlotProperty;
		private static PropertyInfo materialSlotReferenceProperty;
		private static PropertyInfo objectIdProperty;

		private static MethodInfo connectMethod;
		private static MethodInfo connectNoValidateMethod;
		private static MethodInfo getInputSlots_MaterialSlot;
		private static MethodInfo getOutputSlots_MaterialSlot;
		private static MethodInfo getEdges;
		private static MethodInfo removeEdge;
		private static MethodInfo validateGraph;
		private static MethodInfo addValidationError;
		private static MethodInfo clearErrorsForNode;
		private static MethodInfo removeNode;

		public static Assembly sgAssembly;

		private static void GetShaderGraphTypes() {
			sgAssembly = Assembly.Load(new AssemblyName("Unity.ShaderGraph.Editor"));

			materialGraphEditWindowType = sgAssembly.GetType("UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow");
			abstractMaterialNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.AbstractMaterialNode");
			materialSlotType = sgAssembly.GetType("UnityEditor.ShaderGraph.MaterialSlot");
			IEdgeType = sgAssembly.GetType("UnityEditor.Graphing.IEdge");
			colorNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.ColorNode");
		}

		// window:  MaterialGraphEditWindow  member: m_GraphEditorView ->
		// VisualElement:  GraphEditorView   member: m_GraphView  ->
		// GraphView(VisualElement):  MaterialGraphView   
		public static GraphView GetGraphViewFromMaterialGraphEditWindow(EditorWindow win) {
			if (materialGraphEditWindowType == null) {
				GetShaderGraphTypes();
				if (materialGraphEditWindowType == null) return null;
			}

			if (graphEditorViewField == null)
				graphEditorViewField = materialGraphEditWindowType.GetField("m_GraphEditorView", bindingFlags);

			object graphEditorView = graphEditorViewField.GetValue(win);
			if (graphEditorView == null) return null;
			if (graphEditorViewType == null) {
				graphEditorViewType = graphEditorView.GetType();
				graphViewField = graphEditorViewType.GetField("m_GraphView", bindingFlags);
				graphDataField = graphEditorViewType.GetField("m_Graph", bindingFlags);
			}

			// Get Graph View
			GraphView graphView = (GraphView)graphViewField.GetValue(graphEditorView);
			graphData = graphDataField.GetValue(graphEditorView);
			if (graphDataType == null) graphDataType = graphData.GetType();

			return graphView;
		}

		/// <summary>
		/// Converts from GraphView.Node (used for visuals) to the actual Shader Graph Node (a type that inherits AbstractMaterialNode)
		/// </summary>
		public static object NodeToSGMaterialNode(Node node) {
			// SG stores the Material Node in the userData of the VisualElement
			return node.userData;
		}

		/// <summary>
		/// Obtains the values stored in synonyms (serialized by SG). Input should be NodeSGMaterialNode(node)
		/// </summary>
		public static string[] GetSerializedValues(object materialNode) {
			// We store values in the node's "synonyms" field
			// Nodes usually use it for the Search Box so it can display "Float" even if the user types "Vector 1"
			// But it's also serialized in the actual Shader Graph file where it then isn't really used, so it's mine now!~
			if (synonymsField == null) synonymsField = abstractMaterialNodeType.GetField("synonyms");
			return (string[])synonymsField.GetValue(materialNode);
		}

		/// <summary>
		/// Sets the values stored in synonyms (serialized by SG). Input should be NodeSGMaterialNode(node)
		/// </summary>
		public static void SetSerializedValues(object materialNode, string[] values) {
			if (synonymsField == null) synonymsField = abstractMaterialNodeType.GetField("synonyms");
			synonymsField.SetValue(materialNode, values);
		}

		private static string GetSerializedVariableKey(Node node) {
			object materialNode = NodeToSGMaterialNode(node);
			if (materialNode != null) {
				string[] synonyms = GetSerializedValues(materialNode);
				if (synonyms != null && synonyms.Length > 0) {
					return synonyms[0];
				}
			}
			return "";
		}

		private static void SetSerializedVariableKey(Node node, string key) {
			object materialNode = NodeToSGMaterialNode(node);
			SetSerializedValues(materialNode, new string[] { key });
		}

		private static string GetMaterialSlotTypeReflection(Port port) {
			return GetMaterialSlot(port).GetType().ToString();
		}

		internal static object GetMaterialSlot(Port port) {
			// ShaderPort -> MaterialSlot "slot"
			if (shaderPortSlotProperty == null) shaderPortSlotProperty = port.GetType().GetProperty("slot");
			return shaderPortSlotProperty.GetValue(port);
		}

		private static object GetSlotReference(object materialSlot) {
			// MaterialSlot -> SlotReference "slotReference"
			if (materialSlotReferenceProperty == null)
				materialSlotReferenceProperty = materialSlot.GetType().GetProperty("slotReference");
			return materialSlotReferenceProperty.GetValue(materialSlot);
		}

		private static object ConnectReflection(Edge edge, bool noValidate) {
			// GraphData.Connect(SlotReference fromSlotRef, SlotReference toSlotRef)
			if (connectMethod == null)
				connectMethod = graphDataType.GetMethod("Connect");
			if (connectNoValidateMethod == null)
				connectNoValidateMethod = graphDataType.GetMethod("ConnectNoValidate", bindingFlags);

			MethodInfo method = (noValidate) ? connectNoValidateMethod : connectMethod;
			var sgEdge = method.Invoke(graphData, new object[] {
				GetSlotReference(GetMaterialSlot(edge.output)),
				GetSlotReference(GetMaterialSlot(edge.input))
			});

			// Connect returns type of UnityEditor.Graphing.Edge
			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Implementation/Edge.cs
			// It needs to be stored in the userData of the GraphView's Edge VisualElement (in order to support copying nodes)
			edge.userData = sgEdge;

			// Note, it can be null (which will cause an error when trying to copy it) :
			// if either slotRef.node is null
			// if both nodes belong to different graphs
			// if the outputNode is already connected to nodes connected after inputNode (prevents infinite loops)
			// if slot cannot be found in node using slotRef.slotId
			// if both slots are outputs (strangely it doesn't seem to check for both being inputs?)
			return sgEdge;
		}

		private static void DisconnectAllReflection(Node node, int portIndex, Direction direction) {
			object abstractMaterialNode = NodeToSGMaterialNode(node);

			// This all feels pretty hacky, but it works~
#if UNITY_2021_2_OR_NEWER
			// Reflection for : AbstractMaterialNode.GetInputSlots(List<MaterialSlot> list) / GetOutputSlots(List<MaterialSlot> list)
			if (listType_GenericParam0 == null){
				listType_GenericParam0 = typeof(List<>).MakeGenericType(Type.MakeGenericMethodParameter(0));
			}
			if (getInputSlots_MaterialSlot == null) {
				MethodInfo getInputSlots = abstractMaterialNodeType.GetMethod("GetInputSlots", new Type[]{listType_GenericParam0});
				getInputSlots_MaterialSlot = getInputSlots.MakeGenericMethod(materialSlotType);
			}
			if (getOutputSlots_MaterialSlot == null) {
				MethodInfo getOutputSlots = abstractMaterialNodeType.GetMethod("GetOutputSlots", new Type[]{listType_GenericParam0});
				getOutputSlots_MaterialSlot = getOutputSlots.MakeGenericMethod(materialSlotType);
			}
#else
			if (getInputSlots_MaterialSlot == null) {
				MethodInfo getInputSlots = abstractMaterialNodeType.GetMethod("GetInputSlots");
				getInputSlots_MaterialSlot = getInputSlots.MakeGenericMethod(materialSlotType);
			}
			if (getOutputSlots_MaterialSlot == null) {
				MethodInfo getOutputSlots = abstractMaterialNodeType.GetMethod("GetOutputSlots");
				getOutputSlots_MaterialSlot = getOutputSlots.MakeGenericMethod(materialSlotType);
			}
#endif
			if (listType_MaterialSlot == null)
				listType_MaterialSlot = typeof(List<>).MakeGenericType(materialSlotType);
			
			IList materialSlotList = (IList)Activator.CreateInstance(listType_MaterialSlot);
			MethodInfo method = (direction == Direction.Input) ? getInputSlots_MaterialSlot : getOutputSlots_MaterialSlot;
			method.Invoke(abstractMaterialNode, new object[] { materialSlotList });

			object slot = materialSlotList[portIndex]; // Type : (MaterialSlot)
			object slotReference = GetSlotReference(slot);

			// Reflection for : graphData.GetEdges(SlotReference slot, List<IEdge> list)
			if (listType_IEdge == null)
				listType_IEdge = typeof(List<>).MakeGenericType(IEdgeType);
			if (getEdges == null)
				getEdges = graphDataType.GetMethod("GetEdges", new Type[] { slotReference.GetType(), listType_IEdge });

			IList edgeList = (IList)Activator.CreateInstance(listType_IEdge);
			getEdges.Invoke(graphData, new object[] { slotReference, edgeList });

			// For each edge, remove it!
			// Reflection for : graphData.RemoveEdge(IEdge edge)
			// Note : changed to RemoveEdgeNoValidate so it doesn't try to ValidateGraph for every removed edge
			// RemoveEdgeNoValidate(IEdge e, bool reevaluateActivity = true)
			if (removeEdge == null)
				removeEdge = graphDataType.GetMethod("RemoveEdgeNoValidate", bindingFlags);

			foreach (object edge in edgeList) {
				removeEdge.Invoke(graphData, new object[] { edge, true });
			}

			// Now manually trigger ValidateGraph
			//revalidateGraph = true;
		}

		/*
		Seems to cause editor stalls and not sure if it's actually needed?
		There's only one minor visual bug I've noticed when switching between Vector/Float types but I'm not too bothered about that right now.
		/// <summary>
		/// Calls graphData.ValidateGraph() (via Reflection)
		/// </summary>
		public static void ValidateGraph() {
			revalidateGraph = false;
			if (validateGraph == null)
				validateGraph = graphDataType.GetMethod("ValidateGraph");

			validateGraph.Invoke(graphData, null);
		}
		*/

		/// <summary>
		/// Registers to the port's OnConnect and OnDisconnect delegates (via Reflection as they are internal)
		/// </summary>
		public static void RegisterPortDelegates(Port port, Action<Port> OnConnect, Action<Port> OnDisconnect) {
			// internal Action<Port> OnConnect / OnDisconnect;
			if (onConnectField == null)
				onConnectField = typeof(Port).GetField("OnConnect", bindingFlags);
			if (onDisconnectField == null)
				onDisconnectField = typeof(Port).GetField("OnDisconnect", bindingFlags);

			Action<Port> onConnect = (Action<Port>)onConnectField.GetValue(port);
			Action<Port> onDisconnect = (Action<Port>)onDisconnectField.GetValue(port);

			// OnRegisterNodeInputPortConnected, OnRegisterNodeInputPortDisconnected
			onConnectField.SetValue(port, onConnect + OnConnect);
			onDisconnectField.SetValue(port, onDisconnect + OnDisconnect);
		}

		public static void ShowValidationError(Node node, string text) {
			if (objectIdProperty == null)
				objectIdProperty = abstractMaterialNodeType.GetProperty("objectId", bindingFlags);
			if (addValidationError == null)
				addValidationError = graphDataType.GetMethod("AddValidationError");

			object materialNode = NodeToSGMaterialNode(node);
			object objectId = objectIdProperty.GetValue(materialNode);
			addValidationError.Invoke(graphData, new object[]{
				objectId, text, ShaderCompilerMessageSeverity.Error
			});
		}

		public static void ClearErrorsForNode(Node node) {
			if (clearErrorsForNode == null)
				clearErrorsForNode = graphDataType.GetMethod("ClearErrorsForNode");

			object materialNode = NodeToSGMaterialNode(node);
			clearErrorsForNode.Invoke(graphData, new object[] { materialNode });
		}

		public static void RemoveNode(Node node) {
			if (removeNode == null) removeNode = graphDataType.GetMethod("RemoveNode", bindingFlags);
			removeNode.Invoke(graphData, new object[] { NodeToSGMaterialNode(node) });
		}
		#endregion
	}
}