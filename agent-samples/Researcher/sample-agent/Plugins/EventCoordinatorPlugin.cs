// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SemanticKernel;
using Agent365SemanticKernelSampleAgent.Agents;
using System.ComponentModel;
using System.Threading.Tasks;
using System;

namespace Agent365SemanticKernelSampleAgent.Plugins;

/// <summary>
/// Event Coordinator Plugin - Specialized tools for document creation and file management
/// MCP Servers: Microsoft Word, Microsoft SharePoint/OneDrive, Microsoft Teams
/// </summary>
public class EventCoordinatorPlugin
{
    #region Microsoft Word Functions

    [KernelFunction("create_event_proposal"), Description("Create an event proposal document in Microsoft Word with details about the event")]
    public Task<string> CreateEventProposalAsync(
        [Description("Title of the event")] string eventTitle,
        [Description("Date of the event in format MM/DD/YYYY")] string eventDate,
        [Description("Expected number of attendees")] int attendeeCount,
        [Description("Estimated budget for the event")] decimal estimatedBudget)
    {
        // Mock implementation - In production, this would call Microsoft Word MCP server
        var proposalContent = $@"EVENT PROPOSAL

Event: {eventTitle}
Date: {eventDate}
Expected Attendees: {attendeeCount}
Estimated Budget: ${estimatedBudget:N2}

OVERVIEW
This proposal outlines the planning requirements for the upcoming {eventTitle}.

OBJECTIVES
- Coordinate all aspects of event planning
- Ensure smooth execution on event day
- Stay within budget constraints
- Provide excellent attendee experience

NEXT STEPS
1. Obtain budget approval
2. Secure venue booking
3. Send attendee invitations
4. Coordinate catering and AV

Created: {DateTime.Now:MM/dd/yyyy HH:mm}";

        return Task.FromResult($"[EVENT COORDINATOR] ✓ Created event proposal document: '{eventTitle} Proposal.docx'\n\nContent preview:\n{proposalContent}");
    }

    [KernelFunction("create_event_agenda"), Description("Create a detailed event agenda document in Microsoft Word")]
    public Task<string> CreateEventAgendaAsync(
        [Description("Title of the event")] string eventTitle,
        [Description("Comma-separated list of agenda items with times (e.g., '9:00 AM - Welcome, 10:00 AM - Keynote')")] string agendaItems)
    {
        var agendaContent = $@"EVENT AGENDA

{eventTitle}

{agendaItems.Replace(",", "\n")}

All times are subject to change. Please arrive 15 minutes early.

For questions, contact the event coordinator.";

        return Task.FromResult($"[EVENT COORDINATOR] ✓ Created event agenda document: '{eventTitle} Agenda.docx'\n\nAgenda:\n{agendaContent}");
    }

    [KernelFunction("read_document"), Description("Read content from a Microsoft Word document")]
    public Task<string> ReadDocumentAsync(
        [Description("Name or ID of the document to read")] string documentName)
    {
        // Mock implementation
        return Task.FromResult($"[EVENT COORDINATOR] ✓ Retrieved document: '{documentName}'\n\nDocument contains event planning details including venue options, budget estimates, and timeline.");
    }

    #endregion

    #region Microsoft SharePoint/OneDrive Functions

    [KernelFunction("upload_to_sharepoint"), Description("Upload a document or file to Microsoft SharePoint or OneDrive")]
    public Task<string> UploadToSharePointAsync(
        [Description("Name of the file to upload")] string fileName,
        [Description("Folder path in SharePoint (e.g., /Events/Q2-AllHands/)")] string folderPath,
        [Description("Brief description of the file content")] string fileDescription)
    {
        var fullPath = $"{folderPath.TrimEnd('/')}/{fileName}";
        return Task.FromResult($"[EVENT COORDINATOR] ✓ Uploaded '{fileName}' to SharePoint\n  Location: {fullPath}\n  Description: {fileDescription}\n  Access: Team members can now view and edit this file");
    }

    [KernelFunction("list_sharepoint_files"), Description("List files in a SharePoint folder")]
    public Task<string> ListSharePointFilesAsync(
        [Description("Folder path to list files from")] string folderPath)
    {
        // Mock implementation showing typical event planning files
        return Task.FromResult($@"[EVENT COORDINATOR] ✓ Files in '{folderPath}':

1. Event Proposal.docx (Modified: Today, 10:30 AM)
2. Budget Estimates.xlsx (Modified: Today, 11:15 AM)
3. Venue Options.docx (Modified: Yesterday, 3:45 PM)
4. Attendee List.xlsx (Modified: Today, 9:20 AM)
5. Event Agenda.docx (Modified: Today, 2:10 PM)

Total: 5 files");
    }

    [KernelFunction("create_sharepoint_folder"), Description("Create a new folder in SharePoint for organizing event files")]
    public Task<string> CreateSharePointFolderAsync(
        [Description("Path and name of the folder to create (e.g., /Events/Q2-AllHands/)")] string folderPath)
    {
        return Task.FromResult($"[EVENT COORDINATOR] ✓ Created SharePoint folder: '{folderPath}'\n  Team members can now upload files to this location");
    }

    #endregion

    #region Microsoft Teams Functions

    [KernelFunction("post_teams_message"), Description("Post a message to Microsoft Teams channel to coordinate with other agents and stakeholders")]
    public Task<string> PostTeamsMessageAsync(
        [Description("The channel name (e.g., 'Event Planning', 'General')")] string channelName,
        [Description("The message to post")] string message)
    {
        var timestamp = DateTime.Now.ToString("h:mm tt");
        return Task.FromResult($"[EVENT COORDINATOR] ✓ Posted to Teams channel '{channelName}' at {timestamp}:\n\n  📄 {message}\n\n  Other agents and team members will be notified");
    }

    [KernelFunction("read_teams_messages"), Description("Read recent messages from a Microsoft Teams channel")]
    public Task<string> ReadTeamsMessagesAsync(
        [Description("The channel name to read messages from")] string channelName)
    {
        // Mock implementation showing typical coordination messages
        return Task.FromResult($@"[EVENT COORDINATOR] ✓ Recent messages in '{channelName}':

1. Budget & Finance Agent (10 mins ago): 💰 Budget approved - $45,000 allocated
2. Logistics Scheduler Agent (25 mins ago): 📅 Calendar events created for venue booking
3. Event Coordinator (1 hour ago): 📄 Event planning docs uploaded to SharePoint
4. Manager (2 hours ago): Please proceed with Q2 All-Hands planning

Total: 4 recent messages");
    }

    #endregion

    #region Agent Orchestration Functions

    [KernelFunction("analyze_remaining_tasks"), Description("Analyze what tasks remain to be completed for the event planning")]
    public Task<string> AnalyzeRemainingTasksAsync(
        [Description("Current status of event planning")] string currentStatus)
    {
        return Task.FromResult($@"[EVENT COORDINATOR] ✓ Task Analysis Complete

COMPLETED TASKS:
✓ Event proposal document created
✓ Event agenda drafted
✓ Documents uploaded to SharePoint

REMAINING TASKS:
→ Get vendor quotes and budget approval (BUDGET & FINANCE AGENT)
→ Schedule all event activities (LOGISTICS SCHEDULER AGENT)
→ Send calendar invitations (LOGISTICS SCHEDULER AGENT)

NEXT ACTION REQUIRED:
Need Budget & Finance agent to:
1. Email vendors for quotes
2. Convert international currency if needed
3. Get budget approval from finance team

RECOMMENDED HANDOFF: Budget & Finance Agent");
    }

    [KernelFunction("handoff_to_budget_finance"), Description("Hand off the event planning task to the Budget & Finance agent after document creation is complete")]
    public Task<string> HandoffToBudgetFinanceAsync(
        [Description("Summary of what has been completed")] string completedWork,
        [Description("What the Budget & Finance agent needs to do")] string nextSteps)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        return Task.FromResult($@"[EVENT COORDINATOR] ✓ Handing off to Budget & Finance Agent

HANDOFF TIME: {timestamp} PST
FROM: Event Coordinator Agent
TO: Budget & Finance Agent

WORK COMPLETED:
{completedWork}

NEXT STEPS FOR BUDGET AGENT:
{nextSteps}

STATUS: Ready for handoff
CONTEXT: All event planning documents are in SharePoint at /Events/Q2-AllHands/

@BudgetFinanceAgent - You're up! Please proceed with vendor quotes and budget approval.");
    }

    #endregion

    #region Helper Functions

    [KernelFunction("get_event_coordinator_capabilities"), Description("Get a summary of what the Event Coordinator agent can do")]
    public Task<string> GetCapabilitiesAsync()
    {
        return Task.FromResult(@"[EVENT COORDINATOR] My specialized capabilities:

📝 DOCUMENT CREATION (Microsoft Word):
  - Create event proposals
  - Draft event agendas
  - Read and review planning documents

📁 FILE MANAGEMENT (SharePoint/OneDrive):
  - Upload planning documents
  - Organize files in folders
  - List and manage event files

💬 TEAM COORDINATION (Microsoft Teams):
  - Post updates to team channels
  - Read messages from other agents
  - Coordinate with stakeholders

I specialize in creating documentation and managing files for event planning. 
For budget/vendor emails, ask the Budget & Finance agent.
For scheduling/calendars, ask the Logistics Scheduler agent.");
    }

    #endregion
}
