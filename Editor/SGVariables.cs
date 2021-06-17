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
	- Adds "Register Variable" and "Get Variable" nodes (SubGraphs technically)
		If the TextField in both nodes is the same, it creates an invisible connection/wire/edge
		between them. The variables are **local to the graph** - they won't be shared between graphs or subgraphs.
	- It doesn't alter the SG file (except for editing the node's "synonyms" field,
		which is serialized, but isn't used in-graph anyway - only for nodes in the Add Node menu).
		If the tool is removed the graph should still load, However it does 
		use a few SubGraphs and if they don't exist you'll need to remove those nodes,
		reinstall the tool, or at least include the SubGraphs from the tool in your Assets.

Extra Features : (see ExtraFeatures.cs for more info)
	- Group Colors (Right-click group name)
	- 'Port Swap' Hotkey (Default : S)
	- 'Add Node' Hotkeys (Default : Alpha Number keys, 1 to 0)
		- To change nodes : Tools > SGVariablesExtraFeatures > Rebind Node Bindings

	- To edit keybindings : Edit > Shortcuts (search for SGVariables)
		- Note, try to avoid conflicts with SG's hotkeys (mainly A, F and O) as those can't be rebound
		- https://www.cyanilux.com/tutorials/intro-to-shader-graph/#shortcuts

Setup:
	- Put this file in an Editor folder (or install as Package via git url)
	- Make sure the 'Register Variable' and 'Get Variable' SubGraphs are included in the project too

Usage : 
	1) Add Node > Register Variable
		- The node has a text field in the place of it's output port where you can type a variable name.
		- Attach a Float/Vector to the input port.
	2) Add Node > Get Variable
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
		(I tried to fix this but it introduced a possibly worse issue, where if the dynamic port is
		already connected and changes type, it doesn't update the Register Variable node. Need to look into it more)
*/

namespace Cyan {

	[InitializeOnLoad]
	public class SGVariables {

		// Debug ----------------------------------------------

		internal static bool debugMessages 			= true;

		private static bool disableTool 			= false;
		private static bool disableVariableNodes 	= false;
		private static bool disableExtraFeatures 	= false;
		private static bool debugDontHidePorts 		= false;

		//	----------------------------------------------------

		// Sorry if the code is badly organised~

		// TODO Optimise reflection code

		// https://github.com/Unity-Technologies/Graphics/tree/master/com.unity.shadergraph/Editor
		// https://github.com/Unity-Technologies/UnityCsReference/tree/master/Modules/GraphViewEditor

		private static float initTime;

		static SGVariables() {
			if (disableTool) return;
			initTime = Time.realtimeSinceStartup;
			EditorApplication.update += CheckForGraphs;
			Undo.undoRedoPerformed += OnUndoRedo;
		}

		internal static EditorWindow sgWindow;
		internal static EditorWindow prev;
		internal static GraphView graphView;
		internal static bool loadVariables;

		// (Font needed to get string width for variable fields)
		private static Font m_LoadedFont = EditorGUIUtility.LoadRequired("Fonts/Inter/Inter-Regular.ttf") as Font;

		private static void CheckForGraphs() {
			if (Time.realtimeSinceStartup < initTime + 3f) return;

			EditorWindow focusedWindow = EditorWindow.focusedWindow;
			if (focusedWindow == null) return;

			if (focusedWindow.GetType().ToString().Contains("ShaderGraph")) {
				// is Shader Graph
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

				if (!disableVariableNodes) UpdateVariableNodes();
				if (!disableExtraFeatures) ExtraFeatures.UpdateExtraFeatures();
				loadVariables = false;
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
								portType = NodePortType.Vector1;
							}
						}
						if (connectedOutputF != null) {
							if (GetPortType(connectedOutputF).Contains("Vector1")) {
								portType = NodePortType.Vector1;
							}
						}						
	
						SetNodePortType(node, portType);

						if (connectedOutput == null && connectedOutputF == null){
						}else if (connectedOutput == null || connectedOutputF == null){
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
							(connectedInputF != null && !connectedInputF.node.title.Equals("Get Variable"))){
							// Not allowed to connect to the inputs of Register Variable node
							// (unless it's the Get Variable node, which is connected automatically)
							// This can happen if node was created while dragging an edge from an input port
							DisconnectAllOutputs(node);
						}

						// Register methods to port.OnConnect / port.OnDisconnect
						// (is internal so we use reflection)
						inputPorts.ForEach((Port port) => {
							// internal Action<Port> OnConnect;
							FieldInfo onConnectField = typeof(Port).GetField("OnConnect", bindingFlags);
							FieldInfo onDisconnectField = typeof(Port).GetField("OnDisconnect", bindingFlags);
							var onConnect = (Action<Port>)onConnectField.GetValue(port);
							var onDisconnect = (Action<Port>)onDisconnectField.GetValue(port);

							onConnectField.SetValue(port, onConnect + OnRegisterNodeInputPortConnected);
							onDisconnectField.SetValue(port, onDisconnect + OnRegisterNodeInputPortDisconnected);
						});
						// If this breaks an alternative is to just check the ports each frame for different types

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
									if (edge.visible) edge.visible = false;
								}
							}
						};
						outputPorts.ForEach(portAction);

						// Make edges invisible (if not active input port)
						portAction = (Port input) => {
							foreach (var edge in input.connections) {
								if (edge.input != inputPort) {
									if (edge.visible) edge.visible = false;
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
						}

						Port connectedInputF = GetConnectedPort(outputFloat);
						NodePortType portType = GetNodePortType(node);
						if (connectedInputF != null && portType == NodePortType.Vector4){
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

			if (graphView != null) graphView.nodes.ForEach(nodeAction);
		}

		private static void HandlePortUpdates(){
			for (int i = editedPorts.Count - 1; i >= 0; i--) {
				Port port = editedPorts[i];
				Node node = port.node;
				if (node == null) {
					// Node has been deleted, ignore
					editedPorts.RemoveAt(i);
					continue;
				}

				Debug.Log("CHECKING editedPort");

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
							portType = NodePortType.Vector1;
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
						- Unsure how to fix this, but it's also easier just defaulting to Vector4 and if the user wants Float, 
							pass it through a Float node first!
						*/

						Debug.Log("TYPE : " + portType);

						if (inputSlotType.Contains("Vector4") && portType == NodePortType.Vector1) {
							// If port is currently Vector4, but a Float has been attached
							SetNodePortType(node, NodePortType.Vector1);
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
			if (debugDontHidePorts) {
				field.style.top = -35; // put field above (debug)
			} else {
				field.style.top = 39; // put field over first input/output port
			}
			field.StretchToParentWidth();
			// Note : Later we also adjust margins so it doesn't hide the required ports

			//node.ElementAt(0).ElementAt(1).ElementAt(1).style.minHeight = 32;

			var textInput = field.ElementAt(0);
			textInput.style.fontSize = 25;
			textInput.style.unityTextAlign = TextAnchor.MiddleCenter;
			//textInput.style.borderTopColor = new Color(0.13f, 0.13f, 0.13f); // #656565

			field.value = variableName;

			// Add TextField to node VisualElement 
			// Note : This must match what's in TryGetTextField
			node.Insert(1, field);
			return field;
		}

		private static void ResizeNodeToFitText(Node node, string s) {
			float width = 0;
			foreach (char c in s) {
				if (m_LoadedFont.GetCharacterInfo(c, out CharacterInfo info)) {
					width += info.glyphWidth + info.advance;
				}
			}
			node.style.minWidth = width + 45;
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
		enum NodePortType {
			Vector4, Vector1
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
					port.userData = GetPortTypeReflection(port);
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
					port.userData = GetPortTypeReflection(port);
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
			/*
			int n = 0;
			Port p = null;
			Edge brokenEdge = null;
			foreach (Edge edge in port.connections) {
				n++;
				if (edge.parent == null) {
					// weird broken edge?
					brokenEdge = edge;
					continue;
				}
				p = edge.output;
			}

			// whyyy
			if (brokenEdge != null) {
				if (debugMessages) Debug.LogWarning("Broken Edge???");
				brokenEdge.output.Disconnect(brokenEdge);
				port.Disconnect(brokenEdge);
			}

			if (debugMessages) Debug.Log("port " + port.portName + "has " + n + "connections, returned " + ((p != null) ? p.portName : "null"));
			return p;*/
		}

		private static string GetPortType(Port port) {
			string type = (string)port.userData;
			if (type == null) {
				//if (debugMessages) Debug.LogWarning("Port type was null?");
				// Seems this gets reset when a new edge is connected
				// Not a big deal, just wanted to avoid doing reflection every frame
				type = GetPortTypeReflection(port);
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
					currentPortType = NodePortType.Vector1;
				}
			} else {
				if (!IsPortHidden(outputVector)) {
					currentPortType = NodePortType.Vector4;
				} else if (!IsPortHidden(outputFloat)) {
					currentPortType = NodePortType.Vector1;
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
			} else if (portType == NodePortType.Vector1) {
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
			object materialNode = NodeToSGMaterialNode(node);

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

					object objectId = abstractMaterialNodeType.GetProperty("objectId", bindingFlags).GetValue(materialNode);
					graphDataType.GetMethod("AddValidationError").Invoke(graphData, new object[]{
						objectId, "Variable Key is already in use!",
						ShaderCompilerMessageSeverity.Error
					});

					SetSerializedVariableKey(node, "");
					return;
				}
			} else {
				graphDataType.GetMethod("ClearErrorsForNode").Invoke(graphData, new object[] { materialNode });
			}

			// Add new key to Dictionary
			if (newKey) {
				if (debugMessages) Debug.Log("Register " + key);
				variables.Add(key, node);
			}

			// Allow key to be serialised
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
				//DisconnectAllEdges(node, outputPort);
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
			// if (debugMessages) Debug.Log("LinkToAllGetVariableNodes("+key+")");

			List<Node> linkedNodes = new List<Node>();
			Action<Node> nodeAction = (Node n) => {
				if (n.title.Equals("Get Variable")) {
					string key2 = GetSerializedVariableKey(n).ToUpper();
					if (key == key2) {
						// Connect!
						LinkRegisterToGetVariableNode(registerNode, n);
						linkedNodes.Add(n);
					}
				}
			};
			graphView.nodes.ForEach(nodeAction);
			return linkedNodes;
		}

		/// <summary>
		/// Links each output port on the Register Variable node to the input on the Get Variable node
		/// </summary>
		private static void LinkRegisterToGetVariableNode(Node registerNode, Node getNode) {
			if (debugMessages) Debug.Log("Linked Register -> Get");
			var outputPorts = GetOutputPorts(registerNode);
			var inputPorts = GetInputPorts(getNode);
			int portCount = 2;
			// If ports change this needs updating.
			// This assumes the SubGraphs always have the same number of input/output ports,
			// and the order on both nodes is matching types to allow connections
			for (int i = 0; i < portCount; i++) {
				Port outputPort = outputPorts.AtIndex(i);
				Port inputPort = inputPorts.AtIndex(i);
				Connect(outputPort, inputPort);
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
		public static Edge Connect(Port a, Port b) {
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
			object sgEdge = ConnectReflection(edge);
			if (sgEdge == null) {
				// Oh no, something went wrong!
				if (debugMessages) Debug.LogWarning("sgEdge was null! (This is bad as it'll break copying)");
				// This can cause an error here when trying to copy the node :
				// https://github.com/Unity-Technologies/Graphics/blob/3f3263397f0c880135b4f42d623f1510a153e20e/com.unity.shadergraph/Editor/Util/CopyPasteGraph.cs#L149

				object materialNode = NodeToSGMaterialNode(edge.input.node);
				object objectId = abstractMaterialNodeType.GetProperty("objectId", bindingFlags).GetValue(materialNode);
				graphDataType.GetMethod("AddValidationError").Invoke(graphData, new object[]{
					objectId, "Failed to Get Variable! Did you create a loop?",
					ShaderCompilerMessageSeverity.Error
				});
				// Preview may also be incorrect if Register Variable node is float type here

			} else {
				// This attaches the VisualElement with the sg version of the Edge
				// Important to be able to copy the node!
				edge.userData = sgEdge;
			}
			return edge;
		}

		/// <summary>
		/// Disconnects all edges from the specified node and port
		/// </summary>
		public static void DisconnectAllEdges(Node node, Port port) {
			// This disconnects all outputs in port *visually*
			/*
			foreach (Edge edge in port.connections) {
				if (port.direction == Direction.Input) {
					edge.output.Disconnect(edge);
				} else {
					edge.input.Disconnect(edge);
				}
			}
			*/

			int n = 0;
			foreach (Edge edge in port.connections) {
				n++;
			}
			if (n == 0) return;

			port.DisconnectAll();

			int index;
			string methodName;
			if (port.direction == Direction.Input) {
				// The SubGraph input ports have an additional element grouped for when the input is empty, that shows the (0,0,0,0) thing
				VisualElement parent = port.parent;
				index = parent.parent.IndexOf(parent);
				methodName = "GetInputSlots";
			} else {
				index = port.parent.IndexOf(port);
				methodName = "GetOutputSlots";
			}

			// This disconnects all outputs in port in terms of the Shader Graph Data
			DisconnectAllReflection(node, index, methodName);
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
				Connect(toPort, toConnect[i]);
			}
		}
		#endregion

		#region Reflection
		// This probably isn't pretty.
		internal const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
		internal const BindingFlags bindingFlagsFlatten = BindingFlags.FlattenHierarchy | bindingFlags;

		internal static Type materialGraphEditWindowType;
		internal static Type userViewSettingsType;
		internal static Type graphEditorViewType;
		internal static Type graphDataType;
		internal static Type edgeConnectorListenerType;
		internal static Type abstractMaterialNodeType;
		internal static Type materialSlotType;
		internal static Type IEdgeType;
		internal static Type colorNodeType;

		private static VisualElement graphEditorView;
		public static object graphData;
		//private static object edgeConnectorListener;

		public static Assembly sgAssembly;

		private static void GetShaderGraphTypes() {
			sgAssembly = Assembly.Load(new AssemblyName("Unity.ShaderGraph.Editor"));

			materialGraphEditWindowType = sgAssembly.GetType("UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow");
			userViewSettingsType = sgAssembly.GetType("UnityEditor.ShaderGraph.Drawing.UserViewSettings");
			abstractMaterialNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.AbstractMaterialNode");
			materialSlotType = sgAssembly.GetType("UnityEditor.ShaderGraph.MaterialSlot");
			IEdgeType = sgAssembly.GetType("UnityEditor.Graphing.IEdge");
			colorNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.ColorNode");
		}

		// window:  MaterialGraphEditWindow  member: m_GraphEditorView ->
		// VisualElement:  GraphEditorView   member: m_GraphView  ->
		// GraphView(VisualElement):  MaterialGraphView   
		public static GraphView GetGraphViewFromMaterialGraphEditWindow(EditorWindow win) {
			if (materialGraphEditWindowType == null || userViewSettingsType == null) {
				GetShaderGraphTypes();
				if (materialGraphEditWindowType == null) return null;
			}

			FieldInfo visualElementField = materialGraphEditWindowType.GetField("m_GraphEditorView", bindingFlags);
			graphEditorView = (VisualElement)visualElementField.GetValue(win);
			if (graphEditorView == null) return null;
			graphEditorViewType = graphEditorView.GetType();

			// Get Graph View
			FieldInfo graphViewField = graphEditorViewType.GetField("m_GraphView", bindingFlags);
			GraphView graphView = (GraphView)graphViewField.GetValue(graphEditorView);

			FieldInfo graphDataField = graphEditorViewType.GetField("m_Graph", bindingFlags);
			graphData = graphDataField.GetValue(graphEditorView);
			graphDataType = graphData.GetType();

			// EdgeConnectorListener
			/*
			FieldInfo edgeConnectorListenerField = graphEditorViewType.GetField("m_EdgeConnectorListener", bindingFlags);
			edgeConnectorListener = edgeConnectorListenerField.GetValue(graphEditorView);
			edgeConnectorListenerType = edgeConnectorListener.GetType();
			*/

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
			FieldInfo synonymsField = abstractMaterialNodeType.GetField("synonyms");
			return (string[])synonymsField.GetValue(materialNode);
		}

		/// <summary>
		/// Sets the values stored in synonyms (serialized by SG). Input should be NodeSGMaterialNode(node)
		/// </summary>
		public static void SetSerializedValues(object materialNode, string[] values) {
			FieldInfo synonymsField = abstractMaterialNodeType.GetField("synonyms");
			synonymsField.SetValue(materialNode, values);
		}

		private static string GetSerializedVariableKey(Node node) {
			object materialNode = NodeToSGMaterialNode(node);
			if (materialNode != null) {
				//FieldInfo synonymsField = materialNode.GetType().GetField("synonyms");
				//string[] synonyms = (string[])synonymsField.GetValue(materialNode);
				string[] synonyms = GetSerializedValues(materialNode);
				if (synonyms != null && synonyms.Length > 0) {
					return synonyms[0];
				}
			}
			return "";
		}

		private static void SetSerializedVariableKey(Node node, string key) {
			object materialNode = NodeToSGMaterialNode(node);
			//FieldInfo synonymsField = abstractMaterialNodeType.GetField("synonyms");
			//synonymsField.SetValue(materialNode, new string[] { key });
			SetSerializedValues(materialNode, new string[]{ key });
		}

		private static string GetPortTypeReflection(Port port) {
			return GetMaterialSlot(port).GetType().ToString();
		}

		internal static object GetMaterialSlot(Port port) {
			// ShaderPort -> MaterialSlot "slot"
			var v = port.GetType().GetProperty("slot");
			return v.GetValue(port);
		}

		private static object GetSlotReference(object materialSlot) {
			// MaterialSlot -> SlotReference "slotReference"
			var slotRef = materialSlot.GetType().GetProperty("slotReference");
			return slotRef.GetValue(materialSlot);
		}

		private static object ConnectReflection(Edge edge) {
			// This works, but registers some Undo events which we don't really want
			//MethodInfo onDrop = edgeConnectorListenerType.GetMethod("OnDrop", bindingFlags);
			//onDrop.Invoke(edgeConnectorListener, new object[] { graphView, edge });

			// GraphData.Connect(SlotReference fromSlotRef, SlotReference toSlotRef)
			// GetMethod("ConnectNoValidate", bindingFlags)
			var sgEdge = graphDataType.GetMethod("Connect").Invoke(graphData, new object[] {
				GetSlotReference(GetMaterialSlot(edge.output)),
				GetSlotReference(GetMaterialSlot(edge.input))
			});

			// Connect returns type of UnityEditor.Graphing.Edge
			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Implementation/Edge.cs

			// It needs to be stored in the userData of the GraphView's Edge VisualElement (in order to support copying nodes)

			// Note, it can be null (which will cause an error when trying to copy it) :
			// if either slotRef.node is null
			// if both nodes belong to different graphs
			// if the outputNode is already connected to nodes connected after inputNode (prevent infinite loops)
			// if slot cannot be found in node using slotRef.slotId
			// if both slots are outputs
			return sgEdge;
		}

		// This feels *super* hacky, but it works~
		private static void DisconnectAllReflection(Node node, int portIndex, string temp) {
			// node.userData is AbstractMaterialNode
			object abstractMaterialNode = node.userData;

			// Reflection for : AbstractMaterialNode.GetInputSlots(List<MaterialSlot> list) / GetOutputSlots(List<MaterialSlot> list)
			var listType = typeof(List<>);
			var constructedListType = listType.MakeGenericType(materialSlotType);
			var listInstance = (IList)Activator.CreateInstance(constructedListType);
			MethodInfo getInputSlots = abstractMaterialNodeType.GetMethod(temp);
			getInputSlots.MakeGenericMethod(materialSlotType).Invoke(abstractMaterialNode, new object[] { listInstance });

			object slot = listInstance[portIndex]; // Type : (MaterialSlot)

			// Reflection for : graphData.GetEdges(SlotReference slot, List<IEdge> list)
			var constructedListType2 = listType.MakeGenericType(IEdgeType);
			var listInstance2 = (IList)Activator.CreateInstance(constructedListType2);
			object slotReference = slot.GetType().GetProperty("slotReference").GetValue(slot);
			graphDataType.GetMethod("GetEdges", new Type[] { slotReference.GetType(), constructedListType2 })
				.Invoke(graphData, new object[] { slotReference, listInstance2 });

			// For each edge, remove it!
			foreach (object edge in listInstance2) {
				// Reflection for : graphData.RemoveEdge(IEdge edge)
				graphDataType.GetMethod("RemoveEdge").Invoke(graphData, new object[] { edge });
			}
		}
		#endregion
	}
}