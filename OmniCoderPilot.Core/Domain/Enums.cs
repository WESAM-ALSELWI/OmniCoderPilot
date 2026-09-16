namespace OmniCoderPilot.Domain;

public enum ChatRole { System, User, Assistant, Tool }
public enum AgentTaskStatus { Queued, Running, WaitingForApproval, Completed, Failed, Cancelled }
public enum ChangeStatus { Preview, Applied, Rejected }
public enum ToolPermissionMode { Ask, WorkspaceWrite, FullAccess }
