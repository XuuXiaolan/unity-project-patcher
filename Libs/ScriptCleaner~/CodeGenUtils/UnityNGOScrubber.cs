using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nomnom.CodeGenUtils {
    public static class UnityNGOScrubber {
        private static SyntaxNode Scrub__rpcCalls(SyntaxNode root) {
            var methodsToRemove = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text.StartsWith("__getTypeName") || m.Identifier.Text.StartsWith("__initializeVariables") ||
                            m.Identifier.Text.StartsWith("InitializeRPCS_") || m.Identifier.Text.StartsWith("__rpc_handler_"));
            var newRoot = root.RemoveNodes(methodsToRemove, SyntaxRemoveOptions.KeepNoTrivia);
            return newRoot;
        }

        public static void ScrubDecompiledScript(string[] files, bool outputCopy, Action<string> log) {
            foreach (var file in files) {
                try {
                    // delete the copy file if it exists
                    var copyFile = file.Replace(".cs", ".copy.cs");
                    if (File.Exists(copyFile)) {
                        File.Delete(copyFile);
                    }

                    if (!File.Exists(file)) {
                        // log($"[error] File \"{file}\" does not exist");
                        continue;
                    }

                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName == "UnitySourceGeneratedAssemblyMonoScriptTypes_v1") {
                        File.Delete(file);
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(text);
                    var root = tree.GetRoot();
                    root = Scrub__rpcCalls(root);

                    var methods = root.DescendantNodes().OfType<MemberDeclarationSyntax>().ToArray();
                    var methodsToReplace = new List<(MethodDeclarationSyntax, MethodDeclarationSyntax)>();
                    var nodesToRemove = new List<SyntaxNode>();

                    var bannedAttributes = new string[] {
                        "MonoPInvokeCallback"
                    };
                    
                    foreach (var method in methods) {
                        if (method is MethodDeclarationSyntax methodDeclaration) {
                            // log($"[info] - method name: {methodDeclaration.Identifier.Text}");
                            var attributes = methodDeclaration.AttributeLists
                                .SelectMany(x => x.Attributes)
                                .ToArray();

                            if (attributes.Any(x => bannedAttributes.Contains(x.Name.ToString()))) {
                                nodesToRemove.AddRange(attributes);
                                nodesToRemove.Add(methodDeclaration);
                                continue;
                            }
                            
                            var serverRpcAttribute = attributes
                                .FirstOrDefault(x => x.Name.ToString() == "ServerRpc");
                            var clientRpcAttribute = attributes
                                .FirstOrDefault(x => x.Name.ToString() == "ClientRpc");

                            MethodDeclarationSyntax? newMethod = null;
                            if (serverRpcAttribute != null || clientRpcAttribute != null) {
                                newMethod = HandleRpcFunction(methodDeclaration, log);
                            } else {
                                // log("unknown function");
                                continue;
                            }

                            if (newMethod != null) {
                                // log($"[info] new method: {newMethod}");
                                methodsToReplace.Add((methodDeclaration, newMethod));
                            }
                        }
                    }

                    // replace old methods with new methods
                    root = root.ReplaceNodes(methodsToReplace.Select(x => x.Item1), (x, y) => methodsToReplace.First(z => z.Item1 == x).Item2);
                    root = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia);

                    // write the new code back to the file
                    var newCode = root.ToFullString();

                    //todo: remove this from the generic code
                    var startOfRoundClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(x => x.Identifier.Text == "StartOfRound");
                    if (startOfRoundClass != null) {
                        // log($"[class] {startOfRoundClass.Identifier.Text}");
                        newCode = newCode.Replace(
                            @"voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.playerInput.actions.FindAction(""VoiceButton"").IsPressed() && !GameNetworkManager.Instance.localPlayerController.speakingToWalkieTalkie;",
                            @"// voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.playerInput.actions.FindAction(""VoiceButton"").IsPressed() && !GameNetworkManager.Instance.localPlayerController.speakingToWalkieTalkie;"
                        );

                        newCode = newCode.Replace(
                            @"voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.settings.micEnabled;",
                            @"// voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.settings.micEnabled;"
                        );

                        newCode = newCode.Replace(
                            @"if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null || GameNetworkManager.Instance.localPlayerController.isPlayerDead || voiceChatModule.IsMuted || !voiceChatModule.enabled || voiceChatModule == null)",
                            @"if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null || GameNetworkManager.Instance.localPlayerController.isPlayerDead || voiceChatModule == null)"
                        );

                        newCode = newCode.Replace(
                            @"allPlayerScripts[i].gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();",
                            @"// allPlayerScripts[i].gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();"
                        );

                        newCode = newCode.Replace(
                            @"playerControllerB.gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();",
                            @"// playerControllerB.gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();"
                        );
                    }

                    // write to copy file
                    File.WriteAllText(outputCopy ? copyFile : file, newCode);
                    // File.WriteAllText(file, newCode);
                } catch (Exception e) {
                    log($"[error] {e}");
                }
            }
        }

        private static MethodDeclarationSyntax? HandleRpcFunction(MethodDeclarationSyntax methodDeclaration, Action<string> log) {
            var statements = methodDeclaration.Body?.Statements;
            if (statements is not { } validStatements) {
                log($"[error] has no statements");
                return null;
            }

            /*
             * Example:
             * 		NetworkManager networkManager = base.NetworkManager;
		     *      if ((object)networkManager != null && networkManager.IsListening)
		     *      {
             */
            if (validStatements.Count == 2) {
                var secondStatement = validStatements[1];
                if (secondStatement is not IfStatementSyntax secondStatementIf) {
                    log($"[error] no secondStatementIf");
                    return null;
                }

                var childNodes = secondStatementIf.ChildNodes().ToArray();
                // foreach (var child in childNodes) {
                //     log($"[child] is {child.GetType().FullName}: {child}");
                // }

                if (childNodes.Length > 1) {
                    var secondChildNode = childNodes[1];
                    childNodes = secondChildNode.ChildNodes().ToArray();
                    // foreach (var child in childNodes) {
                    //     log($"- [child] is {child.GetType().FullName}: {child}");
                    // }

                    var nestedNode = childNodes.Length == 1 ? childNodes[0] : childNodes[1];
                    if (nestedNode is not IfStatementSyntax nestedNodeIf) {
                        log($"[error] no nestedNodeIf");
                        return null;
                    }
                
                    // strip this if statement of the prefix info and keep the rest
                    var strippedIfStatement = StripIfStatement(nestedNodeIf, log);
                    if (strippedIfStatement != null) {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(SyntaxFactory.Block(strippedIfStatement));
                    
                        return newMethod;
                    } else {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(nestedNodeIf.Statement as BlockSyntax);
                    
                        return newMethod;
                    }
                } else {
                    var thirdStatement = validStatements[3];
                    if (thirdStatement is not IfStatementSyntax thirdStatementIf) {
                        log($"[error] no thirdStatementIf");
                        return null;
                    }
                
                    // strip this if statement of the prefix info and keep the rest
                    var strippedIfStatement = StripIfStatement(thirdStatementIf, log);
                    log("<color=red>[error] not handled yet</color>");
                }
            } 
            /*
             * Example:
             * 		NetworkManager networkManager = base.NetworkManager;
		     *      if ((object)networkManager == null || !networkManager.IsListening)
		     *      {
			 *          return;
		     *      }
		     *      if (__rpc_exec_stage != __RpcExecStage.Client && (networkManager.IsServer || networkManager.IsHost))
		     *      {
			 *          ClientRpcParams clientRpcParams = default(ClientRpcParams);
			 *          FastBufferWriter bufferWriter = __beginSendClientRpc(848048148u, clientRpcParams, RpcDelivery.Reliable);
			 *          bufferWriter.WriteValueSafe(in setBool, default(FastBufferWriter.ForPrimitives));
			 *          bufferWriter.WriteValueSafe(in playSecondaryAudios, default(FastBufferWriter.ForPrimitives));
			 *          BytePacker.WriteValueBitPacked(bufferWriter, playerWhoTriggered);
			 *          __endSendClientRpc(ref bufferWriter, 848048148u, clientRpcParams, RpcDelivery.Reliable);
		     *      }
		     *      if (__rpc_exec_stage != __RpcExecStage.Client || (!networkManager.IsClient && !networkManager.IsHost) || GameNetworkManager.Instance.localPlayerController == null || (playerWhoTriggered != -1 && (int)GameNetworkManager.Instance.localPlayerController.playerClientId == playerWhoTriggered))
             */
            else {
                var fourthStatement = validStatements[3];
                if (fourthStatement is not IfStatementSyntax fourthStatementIf) {
                    log($"[error] no fourthStatementIf");
                    return null;
                }
                
                var strippedIfStatement = StripIfStatement(fourthStatementIf, log);
                if (strippedIfStatement != null) {
                    var remainingStatements = validStatements.Skip(4);
                    var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                        .WithModifiers(methodDeclaration.Modifiers)
                        .WithParameterList(methodDeclaration.ParameterList)
                        .WithAttributeLists(methodDeclaration.AttributeLists)
                        .WithBody(SyntaxFactory.Block(SyntaxFactory.List(remainingStatements.Prepend(strippedIfStatement))));
                    
                    return newMethod;
                } else {
                    var remainingStatements = validStatements.Skip(4).ToArray();
                    
                    // if is empty
                    if (fourthStatementIf.Statement.ChildNodes().FirstOrDefault() is ReturnStatementSyntax) {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(SyntaxFactory.Block(SyntaxFactory.List(remainingStatements)));

                        return newMethod;
                    } else {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(SyntaxFactory.Block(SyntaxFactory.List(remainingStatements).Prepend(fourthStatementIf.Statement)));

                        return newMethod;
                    }
                }
            }

            return null;
        }

        private static IfStatementSyntax? StripIfStatement(IfStatementSyntax ifStatementSyntax, Action<string> log) {
            var conditionString = ifStatementSyntax.Condition.ToString();
            // log($"[from] \"{conditionString}\"");
            foreach (var c in StripConditions) {
                conditionString = conditionString.Replace(c, string.Empty).TrimStart();
            }

            // log($"[to1] \"{conditionString}\"");

            if (string.IsNullOrEmpty(conditionString)) {
                // empty if statement
                // log("empty if statement!");
                return null;
            } else if (conditionString.StartsWith("||") || conditionString.StartsWith("&&")) {
                conditionString = conditionString[2..].TrimStart();
                ifStatementSyntax = SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression(conditionString), ifStatementSyntax.Statement);
            }
            
            // log($"[to2] \"{conditionString}\"");
            
            return ifStatementSyntax;
        }

        private readonly static string[] StripConditions = {
            "__rpc_exec_stage != __RpcExecStage.Client || (!networkManager.IsClient && !networkManager.IsHost)",
            "__rpc_exec_stage != __RpcExecStage.Client || (!networkManager.IsClient && !networkManager.IsHost) || NetworkManager.Singleton == null",
            "__rpc_exec_stage == __RpcExecStage.Client && (networkManager.IsClient || networkManager.IsHost)",
            "__rpc_exec_stage != __RpcExecStage.Server || (!networkManager.IsServer && !networkManager.IsHost)",
            "__rpc_exec_stage == __RpcExecStage.Server && (networkManager.IsServer || networkManager.IsHost)",
            "__rpc_exec_stage == __RpcExecStage.Client && !networkManager.IsClient && networkManager.IsHost"
        };
    }

    public class RemoveCtorMethodCalls : CSharpSyntaxRewriter {
        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node) {
            if (node.Expression is InvocationExpressionSyntax invocation &&
                invocation.Expression.ToString().Contains("ctor")) {
                // If the expression is a method call containing "ctor", remove it.
                return null;
            } else {
                // Otherwise, keep the original node.
                return base.VisitExpressionStatement(node);
            }
        }
    }
}
