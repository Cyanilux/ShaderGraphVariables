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
// Author : Cyanilux (https://twitter.com/Cyanilux)

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
	- If a node uses a DynamicVector/DynamicValue slot (Most math nodes) it will default to Vector4.
		If you want Float, pass the value through the Float node before connecting!
		
		(I tried to fix this but it introduced a possibly worse issue, where if the dynamic port is
		already connected and changes type, it doesn't update the Register Variable node.
		Unsure if there's a callback to handle this, so not fixing for now. Use the Float node)

	- If a 'Get Variable' node is connected to the vertex stage and then a name is entered, it can cause shader errors
		if fragment-only nodes are used by the variable (e.g. cannot map expression to vs_5_0 instruction set)

*/

[InitializeOnLoad]
public class SGVariables {

	// Debug ----------------------------------------------

	private static bool disableTool = false;
	private static bool disableVariableNodes = false;
	private static bool disableExtraFeatures = false;
	private static bool debugMessages = true;
	private static bool debugDontHidePorts = false;

	//	----------------------------------------------------

	// TODO Optimise reflection code

	// Extra features :
	// Group colours
	// Hotkey for switching the A/B input ports around for math based nodes.
	// Hotkey for adding Split node

	// https://github.com/Unity-Technologies/Graphics/tree/master/com.unity.shadergraph/Editor
	// https://github.com/Unity-Technologies/UnityCsReference/tree/master/Modules/GraphViewEditor

	private static EditorWindow sgWindow;
	private static EditorWindow prev;
	private static float initTime;
	private static GraphView graphView;
	private static bool loadVariables;

	// Don't know how else to obtain a reference to this
	private static Font m_LoadedFont = EditorGUIUtility.LoadRequired("Fonts/Inter/Inter-Regular.ttf") as Font;

	private static List<Port> editedPorts = new List<Port>();

	static SGVariables() {
		if (disableTool) return;
		initTime = Time.realtimeSinceStartup;
		EditorApplication.update += CheckForGraphs;
		Undo.undoRedoPerformed += OnUndoRedo;
	}

	private static void OnUndoRedo() {
		// Undo/Redo redraws the graph, so will cause "key already in use" errors.
		// Doing this will trigger the variables to be reloaded
		prev = null;
		initTime = Time.realtimeSinceStartup;
	}

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
			if (!disableExtraFeatures) UpdateExtraFeatures();
			loadVariables = false;
		}
	}

	private static void UpdateVariableNodes() {

		for (int i = editedPorts.Count - 1; i >= 0; i--) {
			Port port = editedPorts[i];
			Node node = port.node;
			if (node == null) {
				// Node has been deleted, ignore
				editedPorts.RemoveAt(i);
				continue;
			}

			Port outputConnectedToInput = GetPortConnectedToInput(port);
			if (outputConnectedToInput != null) {
				Debug.Log(outputConnectedToInput.portName + " > " + port.portName);

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
					Debug.Log(connectedSlotType + " > " + inputSlotType);

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

		Action<Node> nodeAction = (Node node) => {
			if (node == null) return;
			if (node.title.Equals("Register Variable")) {
				TextField field = TryGetTextField(node);
				if (field == null) {
					Debug.Log("Register Variable > Added Text Field");
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

					Port connectedOutput = GetPortConnectedToInput(inputVector);
					NodePortType portType = NodePortType.Vector4;
					if (connectedOutput != null) {
						if (GetPortType(connectedOutput).Contains("Vector1")) {
							portType = NodePortType.Vector1;
						}
					}
					SetNodePortType(node, portType);

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
					/*
					Port outputPort = GetActivePort(outputPorts);

					Port outputConnectedToInput = GetPortConnectedToInput(inputPort);
					if (outputConnectedToInput != null) {
						string portType = GetPortType(outputConnectedToInput);
						string inputPortType = GetPortType(inputPort);

						if (portType != inputPortType) {
							Debug.Log(portType + " > " + inputPortType);

							if (inputPortType.Contains("Vector4") && portType.Contains("Vector1")) {
								// If port is currently Vector4, but a Float has been attached
								SetNodePortType(node, NodePortType.Vector1, outputConnectedToInput);
							} else if (inputPortType.Contains("Vector1") && !portType.Contains("Vector1")) {
								// If port is currently Float, but a Vector2/3/4 has been attached
								//TODO find a way to stop this being triggered every frame with Vector2/3
								SetNodePortType(node, NodePortType.Vector4, outputConnectedToInput);
							}
						}
					}
					*/

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
					if (!IsPortHidden(outputVector) && !IsPortHidden(outputFloat)) {
						string key = GetSerializedVariableKey(node);
						Get(key, node);
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
					*/
				}
			}
		};

		if (graphView != null) graphView.nodes.ForEach(nodeAction);
	}

	private static void OnRegisterNodeInputPortConnected(Port port) {
		if (IsPortHidden(port)) return; // If hidden, ignore connections (also used to prevent infinite loop)

		Debug.Log("OnRegisterNodeInputPort Connected (" + port.portName + ")");

		// Sadly it seems we can't edit connections directly here,
		// It errors as a collection is modified while SG is looping over it.
		// To avoid this, we'll assign the port to a list and check it during the EditorApplication.update
		// Note however this delayed action causes the edge to glitch out a bit.
		editedPorts.Add(port);
	}

	private static void OnRegisterNodeInputPortDisconnected(Port port) {
		if (IsPortHidden(port)) return; // If hidden, ignore connections (also used to prevent infinite loop)
		Debug.Log("OnRegisterNodeInputPort Disconnected (" + port.portName + ")");

		//DisconnectAllInputs(port.node);
		editedPorts.Add(port);
	}

	enum NodePortType {
		Vector4, Vector1
	}

	#region Get Input/Output Ports
	private static UQueryState<Port> GetInputPorts(Node node) {
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

	private static UQueryState<Port> GetOutputPorts(Node node) {
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

	private static Port GetPortConnectedToInput(Port port) {
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
			Debug.Log("Removed Broken Edge");
			brokenEdge.output.Disconnect(brokenEdge);
			port.Disconnect(brokenEdge);
		}

		Debug.Log("port " + port.portName + "has " + n + "connections, returned " + ((p != null) ? p.portName : "null"));
		return p;
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

	#region Register/Get Variables
	private static Dictionary<string, Node> variables = new Dictionary<string, Node>();

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

	private static List<Node> LinkToAllGetVariableNodes(string key, Node registerNode) {
		// Debug.Log("LinkToAllGetVariableNodes("+key+")");

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
		Debug.Log("Linked Register -> Get");
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
	/// For some reason this results in duplicating the edge next frame though :\
	/// </summary>
	private static Edge Connect(Port a, Port b) {
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
	private static void DisconnectAllEdges(Node node, Port port) {
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
	private static void DisconnectAllInputs(Node node) {
		var inputPorts = GetInputPorts(node);
		inputPorts.ForEach((Port port) => {
			DisconnectAllEdges(node, port);
		});
	}

	/// <summary>
	/// Disconnects all edges in all output ports on node
	/// </summary>
	private static void DisconnectAllOutputs(Node node) {
		var outputPorts = GetOutputPorts(node);
		outputPorts.ForEach((Port port) => {
			DisconnectAllEdges(node, port);
		});
	}

	private static void MoveAllOutputs(Node node, Port port, Port toPort) {
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
	const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
	const BindingFlags bindingFlagsFlatten = BindingFlags.FlattenHierarchy | bindingFlags;

	private static Type materialGraphEditWindowType;
	private static Type userViewSettingsType;
	private static Type graphEditorViewType;
	private static Type graphDataType;
	private static Type edgeConnectorListenerType;
	private static Type abstractMaterialNodeType;
	private static Type materialSlotType;
	private static Type IEdgeType;
	private static Type colorNodeType;

	private static VisualElement graphEditorView;
	private static object graphData;
	private static object edgeConnectorListener;

	private static Type colorPickerType;

	static void GetShaderGraphTypes() {
		Assembly assembly = Assembly.Load(new AssemblyName("Unity.ShaderGraph.Editor"));

		materialGraphEditWindowType = assembly.GetType("UnityEditor.ShaderGraph.Drawing.MaterialGraphEditWindow");
		userViewSettingsType = assembly.GetType("UnityEditor.ShaderGraph.Drawing.UserViewSettings");
		abstractMaterialNodeType = assembly.GetType("UnityEditor.ShaderGraph.AbstractMaterialNode");
		materialSlotType = assembly.GetType("UnityEditor.ShaderGraph.MaterialSlot");
		IEdgeType = assembly.GetType("UnityEditor.Graphing.IEdge");
		colorNodeType = assembly.GetType("UnityEditor.ShaderGraph.ColorNode");

		Assembly assembly2 = Assembly.Load(new AssemblyName("UnityEditor"));
		colorPickerType = assembly2.GetType("UnityEditor.ColorPicker");
	}

	// window:  MaterialGraphEditWindow  member: m_GraphEditorView ->
	// VisualElement:  GraphEditorView   member: m_GraphView  ->
	// GraphView(VisualElement):  MaterialGraphView   
	static GraphView GetGraphViewFromMaterialGraphEditWindow(EditorWindow win) {
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

		FieldInfo edgeConnectorListenerField = graphEditorViewType.GetField("m_EdgeConnectorListener", bindingFlags);
		edgeConnectorListener = edgeConnectorListenerField.GetValue(graphEditorView);
		edgeConnectorListenerType = edgeConnectorListener.GetType();

		return graphView;
	}

	private static object NodeToSGMaterialNode(Node node) {
		// GraphView uses "Node" for visual stuff, but SG has AbstractMaterialNode and nodes that inherit from it.
		// SG stores the Material Node in the userData of the VisualElement
		return node.userData;
	}

	private static string GetSerializedVariableKey(Node node) {
		// We store the key in the node's "synonyms" field
		// Nodes usually use it for the Search Box so it can display "Float" even if the user types "Vector 1"
		// But it's also serialized in the actual Shader Graph file where it then isn't really used, so it's mine now!~
		object materialNode = NodeToSGMaterialNode(node);
		if (materialNode != null) {
			FieldInfo synonymsField = materialNode.GetType().GetField("synonyms");
			string[] synonyms = (string[])synonymsField.GetValue(materialNode);
			if (synonyms != null && synonyms.Length > 0) {
				return synonyms[0];
			}
		}
		return "";
	}

	private static void SetSerializedVariableKey(Node node, string key) {
		object materialNode = NodeToSGMaterialNode(node);
		FieldInfo synonymsField = abstractMaterialNodeType.GetField("synonyms");
		synonymsField.SetValue(materialNode, new string[] { key });
	}

	private static string GetPortTypeReflection(Port port) {
		return GetMaterialSlot(port).GetType().ToString();
	}

	private static object GetMaterialSlot(Port port) {
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

	#region Extra Features

	[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Swap A & B Ports _#s")]
	static void FirstCommand() {
		Debug.Log("You used the shortcut!");

		List<ISelectable> selected = graphView.selection;
		foreach (ISelectable s in selected) {
			if (s is Node) {
				Node node = (Node)s;
				var materialNode = NodeToSGMaterialNode(node);
				string type = materialNode.GetType().ToString();
				type = type.Substring(type.LastIndexOf('.')+1);
				Debug.Log(type);
				if (type == "AddNode" || type == "SubtractNode" ||
					type == "MultiplyNode" || type == "DivideNode" ||
					type == "MaximumNode" || type == "MinimumNode" ||
					type == "LerpNode" || type == "InverseLerpNode" ||
					type == "StepNode" || type == "SmoothstepNode") {
					var inputPorts = GetInputPorts(node);
					Port a = inputPorts.AtIndex(0);
					Port b = inputPorts.AtIndex(1);

					// Swap connections
					Port connectedA = GetPortConnectedToInput(a);
					Port connectedB = GetPortConnectedToInput(b);
					DisconnectAllInputs(node);
					if (connectedA != null) Connect(connectedA, b);
					if (connectedB != null) Connect(connectedB, a);

					// Swap values (the values used if no port is connected)
					var aSlot = GetMaterialSlot(a);
					var bSlot = GetMaterialSlot(b);
					var valueProperty = aSlot.GetType().GetProperty("value");
					var valueA = valueProperty.GetValue(aSlot);
					var valueB = valueProperty.GetValue(bSlot);
					valueProperty.SetValue(aSlot, valueB);
					valueProperty.SetValue(bSlot, valueA);
					
					if (connectedA == null && connectedB == null){
						// If there's no connections we need to update values ourself
						//graphDataType.GetMethod("ValidateGraph").Invoke(graphData, null);
						//abstractMaterialNodeType.GetMethod("Dirty").Invoke(materialNode, new object[]{1});
						//graphDataType.GetMethod("ValidateGraph").Invoke(graphData, null);
					}

				}
			}
		}
	}

	private static void UpdateExtraFeatures() {
		//if (loadVariables) { // (first time load, but we kinda need to constantly check as groups could be copied)
		// Load Group Colours
		graphView.nodes.ForEach((Node node) => {
			if (node.title.Equals("Color") && node.visible) {
				if (GetSerializedVariableKey(node) == "GroupColor") {
					var scope = node.GetContainingScope();
					if (scope == null) {
						// Node is not in group, the group may have been deleted.
						// GraphData.RemoveNode(abstractMaterialNode);
						MethodInfo addNode = graphDataType.GetMethod("RemoveNode", bindingFlags);
						addNode.Invoke(graphData, new object[] { NodeToSGMaterialNode(node) });
						return;
					}
					node.visible = false;
					node.style.maxWidth = 100;
					SetGroupColor(scope as Group, node, GetGroupNodeColor(node));
				}
			}
		});
		//}

		// As we right-click group, add the manipulator
		List<ISelectable> selected = graphView.selection;
		foreach (ISelectable s in selected) {
			if (s is Group) {
				Group group = (Group)s;

				if (editingGroup != null) {
					editingGroup.RemoveManipulator(manipulator);
				}
				group.AddManipulator(manipulator);
				editingGroup = group;
			}
		}

		// User clicked context menu, show Color Picker
		if (editingGuid != null) {
			Node colorNode = GetGroupColorNode(editingGuid);
			editingGroupColorNode = colorNode;
			if (colorNode != null) {
				if (colorNode.visible) {
					colorNode.visible = false;
					colorNode.style.maxWidth = 100;
				}

				// Couldn't figure out a way to show the colour picker from UIElements, so... reflection!
				// Show(Action<Color> colorChangedCallback, Color col, bool showAlpha = true, bool hdr = false)
				MethodInfo show = colorPickerType.GetMethod("Show",
					new Type[] { typeof(Action<Color>), typeof(Color), typeof(bool), typeof(bool) }
				);
				Action<Color> action = GroupColourChanged;
				show.Invoke(null, new object[] { action, GetGroupNodeColor(editingGroupColorNode), true, false });

				editingGuid = null;
			}
		} else {
			editingGroupColorNode = null;
		}
	}

	static void BuildContextualMenu(ContextualMenuPopulateEvent evt) {
		evt.menu.AppendSeparator();
		evt.menu.AppendAction("Edit Group Color", OnMenuAction, DropdownMenuAction.AlwaysEnabled);
	}

	static void OnMenuAction(DropdownMenuAction action) {
		if (editingGroup == null) return;
		Debug.Log("OnMenuAction " + editingGroup.title);

		string guid = GetGroupGuid(editingGroup);
		if (guid == null) return;

		Node colorNode = GetGroupColorNode(guid);
		if (colorNode == null) {
			// We store the group colour in a hidden colour node
			CreateGroupColorNode(editingGroup);

			// We need to wait until this node is actually created,
			// so we'll store the guid in editingGuid and handle the rest
			// in UpdateExtraFeatures()
		}

		editingGuid = guid;
	}

	private static ContextualMenuManipulator manipulator = new ContextualMenuManipulator(BuildContextualMenu);

	private static Group editingGroup;
	private static string editingGuid;
	private static Node editingGroupColorNode;

	private static PropertyInfo groupGuidField;

	private static void CreateGroupColorNode(Group group) {
		var groupData = group.userData;
		if (groupData == null) return;

		var nodeToAdd = Activator.CreateInstance(colorNodeType); // Type : ColorNode

		// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Nodes/AbstractMaterialNode.cs

		FieldInfo synonymsField = abstractMaterialNodeType.GetField("synonyms");
		synonymsField.SetValue(nodeToAdd, new string[] { "GroupColor" });

		var a = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
		var colorStruct = a.GetValue(nodeToAdd);
		var b = colorStruct.GetType().GetField("color");
		b.SetValue(colorStruct, new Color(0.1f, 0.1f, 0.1f, 0.1f));
		a.SetValue(nodeToAdd, colorStruct);

		var groupProperty = abstractMaterialNodeType.GetProperty("group", bindingFlags); // Type : GroupData
		var drawStateProperty = abstractMaterialNodeType.GetProperty("drawState", bindingFlags); // Type : DrawState
		var previewExpandedProperty = abstractMaterialNodeType.GetProperty("previewExpanded", bindingFlags); // Type : Bool

		previewExpandedProperty.SetValue(nodeToAdd, false);
		var drawState = drawStateProperty.GetValue(nodeToAdd);
		var positionProperty = drawState.GetType().GetProperty("position", bindingFlags);
		var expandedProperty = drawState.GetType().GetProperty("expanded", bindingFlags);
		Rect r = group.GetPosition();
		r.x += 25;
		r.y += 60;
		positionProperty.SetValue(drawState, r);
		expandedProperty.SetValue(drawState, false);
		drawStateProperty.SetValue(nodeToAdd, drawState);
		groupProperty.SetValue(nodeToAdd, groupData);

		// GraphData.AddNode(abstractMaterialNode)
		MethodInfo addNode = graphDataType.GetMethod("AddNode", bindingFlags);
		addNode.Invoke(graphData, new object[] { nodeToAdd });
	}

	private static string GetGroupGuid(Scope scope) {
		var groupData = scope.userData;
		if (groupData == null) return null;
		if (groupGuidField == null) groupGuidField = groupData.GetType().GetProperty("objectId", bindingFlags);
		var groupGuid = (string)groupGuidField.GetValue(groupData);
		Debug.Log("GroupGuid : " + groupGuid);
		return groupGuid;
	}

	private static Node GetGroupColorNode(string guid) {
		List<Node> nodes = graphView.nodes.ToList();
		foreach (Node node in nodes) {
			if (node.title.Equals("Color")) {
				var scope = node.GetContainingScope();
				if (scope == null) continue; // Node is not in group
				if (GetGroupGuid(scope) == guid && GetSerializedVariableKey(node) == "GroupColor") {
					Debug.Log("Found node in group~");
					//node.visible = false;
					//node.style.maxWidth = 100;
					return node;
				}
			}
		}
		return null;
	}

	private static Color GetGroupNodeColor(Node node) {
		var materialNode = NodeToSGMaterialNode(node);
		var a = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
		var colorStruct = a.GetValue(materialNode);
		var b = colorStruct.GetType().GetField("color");
		return (Color)b.GetValue(colorStruct);
	}

	private static void SetGroupColor(Group group, Node groupNode, Color color) {
		group.style.backgroundColor = color;

		var materialNode = NodeToSGMaterialNode(groupNode);
		var a = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
		var colorStruct = a.GetValue(materialNode);
		var b = colorStruct.GetType().GetField("color");
		b.SetValue(colorStruct, color);
		a.SetValue(materialNode, colorStruct);

		var label = group.Query<UnityEngine.UIElements.Label>().First();
		if (color.grayscale > 0.5 && color.a > 0.4) {
			//label.style.backgroundColor = new Color(0,0,0,0.5f);
			label.style.color = Color.black;
		} else {
			label.style.color = Color.white;
		}
	}

	private static void GroupColourChanged(Color color) {
		SetGroupColor(editingGroup, editingGroupColorNode, color);
	}
	#endregion

}
