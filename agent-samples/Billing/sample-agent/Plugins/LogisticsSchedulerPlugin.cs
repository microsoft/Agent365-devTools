// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SemanticKernel;
using Agent365SemanticKernelSampleAgent.Agents;
using System.ComponentModel;
using System.Threading.Tasks;
using System;

namespace Agent365SemanticKernelSampleAgent.Plugins;

/// <summary>
/// Logistics Scheduler Plugin - Specialized tools for calendar management and event scheduling
/// MCP Servers: Microsoft Outlook Calendar, Microsoft SharePoint/OneDrive (read-only), Microsoft Teams
/// </summary>
public class LogisticsSchedulerPlugin
{
    #region Microsoft Outlook Calendar Functions

    [KernelFunction("create_calendar_event"), Description("Create a calendar event in Microsoft Outlook for event sessions, meetings, or activities")]
    public Task<string> CreateCalendarEventAsync(
        [Description("Title of the calendar event")] string eventTitle,
        [Description("Start date and time (MM/DD/YYYY HH:MM AM/PM)")] string startDateTime,
        [Description("End date and time (MM/DD/YYYY HH:MM AM/PM)")] string endDateTime,
        [Description("Location or room for the event")] string location,
        [Description("Comma-separated list of attendee emails")] string? attendees = null)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        var attendeeList = string.IsNullOrEmpty(attendees) ? "No attendees specified" : attendees;
        
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Calendar event created successfully

Event: {eventTitle}
Start: {startDateTime}
End: {endDateTime}
Location: {location}
Attendees: {attendeeList}

Created: {timestamp}
Status: Invitations sent
Meeting ID: MTG-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}

Attendees will receive calendar invitations via email.");
    }

    [KernelFunction("schedule_venue_booking"), Description("Create a calendar event for venue booking and access")]
    public Task<string> ScheduleVenueBookingAsync(
        [Description("Venue name")] string venueName,
        [Description("Date of booking (MM/DD/YYYY)")] string bookingDate,
        [Description("Access time (HH:MM AM/PM)")] string accessTime,
        [Description("Event duration in hours")] int durationHours)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Venue booking scheduled

Venue: {venueName}
Date: {bookingDate}
Access Time: {accessTime}
Duration: {durationHours} hours
Event Type: Venue Access & Setup

Calendar Status: Confirmed
Reminder: Set for 1 day before event

Note: Coordinate with venue staff for key pickup and setup requirements.");
    }

    [KernelFunction("schedule_catering_delivery"), Description("Create a calendar event for catering delivery and setup")]
    public Task<string> ScheduleCateringDeliveryAsync(
        [Description("Catering company name")] string cateringCompany,
        [Description("Delivery date (MM/DD/YYYY)")] string deliveryDate,
        [Description("Delivery time (HH:MM AM/PM)")] string deliveryTime,
        [Description("Number of people to serve")] int attendeeCount)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Catering delivery scheduled

Provider: {cateringCompany}
Date: {deliveryDate}
Delivery Time: {deliveryTime}
Servings: {attendeeCount} people
Event Type: Catering Delivery & Setup

Calendar Status: Confirmed
Reminder: Set for 2 hours before delivery

Action Items:
✓ Ensure kitchen/serving area is accessible
✓ Coordinate with building security for delivery access
✓ Have contact person available for delivery");
    }

    [KernelFunction("schedule_av_setup"), Description("Create a calendar event for Audio/Visual equipment setup")]
    public Task<string> ScheduleAvSetupAsync(
        [Description("A/V company or technician name")] string avProvider,
        [Description("Setup date (MM/DD/YYYY)")] string setupDate,
        [Description("Setup time (HH:MM AM/PM)")] string setupTime,
        [Description("Equipment details")] string equipmentDetails)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ A/V setup scheduled

Provider: {avProvider}
Date: {setupDate}
Setup Time: {setupTime}
Equipment: {equipmentDetails}
Event Type: A/V Equipment Setup

Calendar Status: Confirmed
Reminder: Set for 3 hours before setup

Setup Checklist:
✓ Projector and screen
✓ Microphones and sound system
✓ Video conferencing equipment
✓ Technical rehearsal scheduled");
    }

    [KernelFunction("get_calendar_events"), Description("Retrieve upcoming calendar events for event planning")]
    public Task<string> GetCalendarEventsAsync(
        [Description("Optional: Filter by date range (e.g., 'this week', 'March 2026')")] string? dateRange = null)
    {
        var filter = string.IsNullOrEmpty(dateRange) ? "upcoming events" : $"events for {dateRange}";
        
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Retrieved {filter}:

1. Q2 All-Hands Meeting
   Date: March 15, 2026, 9:00 AM - 5:00 PM
   Location: Premium Events Center
   Attendees: 150 people
   
2. Catering Delivery
   Date: March 15, 2026, 8:00 AM
   Location: Premium Events Center - Kitchen
   Provider: Deluxe Food Catering
   
3. A/V Equipment Setup
   Date: March 15, 2026, 7:30 AM
   Location: Premium Events Center - Main Hall
   Provider: AV Studio Tech
   
4. Venue Access & Setup
   Date: March 15, 2026, 7:00 AM
   Location: Premium Events Center
   Duration: 12 hours

Total scheduled events: 4");
    }

    [KernelFunction("update_calendar_event"), Description("Update an existing calendar event with new details")]
    public Task<string> UpdateCalendarEventAsync(
        [Description("Event ID or title to update")] string eventIdentifier,
        [Description("What to update (time, location, attendees, etc.)")] string updateType,
        [Description("New value for the update")] string newValue)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Calendar event updated

Event: {eventIdentifier}
Updated Field: {updateType}
New Value: {newValue}
Updated: {timestamp}

Status: All attendees notified of change
Meeting update sent via email");
    }

    #endregion

    #region Microsoft SharePoint Functions (Read-Only)

    [KernelFunction("read_logistics_documents"), Description("Read logistics documents and attendee lists from SharePoint (read-only access)")]
    public Task<string> ReadLogisticsDocumentsAsync(
        [Description("Document name or path in SharePoint")] string documentPath)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Retrieved document from SharePoint: '{documentPath}'

LOGISTICS TIMELINE:

7:00 AM - Venue Access
  - Building opens
  - Setup team arrives
  
7:30 AM - A/V Setup
  - AV Studio Tech arrives
  - Equipment installation begins
  
8:00 AM - Catering Delivery
  - Deluxe Food Catering arrives
  - Food setup in kitchen area
  
8:30 AM - Final Checks
  - Test all A/V equipment
  - Verify seating arrangements
  
9:00 AM - EVENT START
  - Attendees begin arriving
  - Registration desk open

Permission: Read-Only
Last Modified: {DateTime.Now.AddHours(-2):MM/dd/yyyy h:mm tt}");
    }

    [KernelFunction("access_attendee_list"), Description("Access the attendee list from SharePoint for scheduling purposes")]
    public Task<string> AccessAttendeeListAsync()
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Attendee list accessed from SharePoint

ATTENDEE SUMMARY:
Total Registered: 142 / 150
Confirmed: 128
Pending: 14
Declined: 8

Dietary Requirements:
  - Vegetarian: 23
  - Vegan: 12
  - Gluten-free: 8
  - No restrictions: 99

Special Accommodations:
  - Wheelchair access: 3
  - Sign language interpreter: 1

Permission: Read-Only
For updates, contact Event Coordinator agent");
    }

    #endregion

    #region Microsoft Teams Functions

    [KernelFunction("post_scheduling_update"), Description("Post a scheduling update to Microsoft Teams channel")]
    public Task<string> PostSchedulingUpdateAsync(
        [Description("The channel name")] string channelName,
        [Description("Scheduling update message")] string message)
    {
        var timestamp = DateTime.Now.ToString("h:mm tt");
        return Task.FromResult($"[LOGISTICS SCHEDULER] ✓ Posted to Teams channel '{channelName}' at {timestamp}:\n\n  📅 {message}\n\n  Team notified of scheduling changes");
    }

    [KernelFunction("read_teams_messages"), Description("Read messages from Microsoft Teams channel")]
    public Task<string> ReadTeamsMessagesAsync(
        [Description("The channel name to read from")] string channelName)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Recent messages in '{channelName}':

1. Event Coordinator (20 mins ago): 📄 Final agenda uploaded to SharePoint
2. Budget & Finance (45 mins ago): 💰 All vendor payments confirmed
3. Logistics Scheduler (1 hour ago): 📅 All calendar invites sent successfully
4. Manager (3 hours ago): Reminder: Event is March 15th

Total: 4 recent messages");
    }

    [KernelFunction("coordinate_with_team"), Description("Send a coordination request to other agents via Teams")]
    public Task<string> CoordinateWithTeamAsync(
        [Description("The channel name")] string channelName,
        [Description("Which agent or team member to coordinate with")] string targetAgent,
        [Description("What needs to be coordinated")] string coordinationRequest)
    {
        var timestamp = DateTime.Now.ToString("h:mm tt");
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Coordination request sent in '{channelName}' at {timestamp}

To: @{targetAgent}
Request: {coordinationRequest}

Status: Message delivered
Expected response time: 15-30 minutes");
    }

    #endregion

    #region Agent Orchestration Functions

    [KernelFunction("analyze_remaining_tasks"), Description("Analyze what scheduling and logistics tasks remain to be completed")]
    public Task<string> AnalyzeRemainingTasksAsync(
        [Description("Current scheduling status")] string currentStatus)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Task Analysis Complete

COMPLETED TASKS:
✓ Main event calendar entry created
✓ Venue booking scheduled
✓ Catering delivery scheduled
✓ A/V setup scheduled
✓ Calendar invitations sent to all 150 attendees
✓ Complete event timeline created

REMAINING TASKS:
✓ ALL TASKS COMPLETE

EVENT PLANNING STATUS:
→ Event Coordinator: Documents created and organized ✓
→ Budget & Finance: Vendor quotes obtained and budget approved ✓
→ Logistics Scheduler: All scheduling complete ✓

FINAL STATUS:
The Q2 All-Hands Meeting is fully planned and ready for March 15, 2026!
All 150 attendees have been invited and all logistics are confirmed.

NO FURTHER HANDOFF NEEDED - Task Complete!");
    }

    [KernelFunction("complete_event_planning"), Description("Mark the event planning as complete and notify all stakeholders")]
    public Task<string> CompleteEventPlanningAsync(
        [Description("Event name")] string eventName,
        [Description("Summary of all completed work")] string completionSummary)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ EVENT PLANNING COMPLETE

COMPLETION TIME: {timestamp} PST
EVENT: {eventName}
STATUS: ✅ FULLY PLANNED AND READY

COMPLETE WORKFLOW SUMMARY:
{completionSummary}

FINAL CHECKLIST:
✓ Event proposal and agenda created
✓ All documents stored in SharePoint
✓ Vendor quotes obtained and approved
✓ Budget finalized: $37,400 of $45,000
✓ International currency converted
✓ Venue booking confirmed
✓ Catering scheduled for delivery
✓ A/V equipment setup scheduled
✓ 150 calendar invitations sent
✓ Complete timeline documented

EVENT READY FOR: March 15, 2026 at 9:00 AM PST
VENUE: Premium Events Center
ATTENDEES: 150 confirmed

All agents have completed their specialized tasks.
Event planning workflow concluded successfully! 🎉");
    }

    #endregion

    #region Helper Functions

    [KernelFunction("get_logistics_capabilities"), Description("Get a summary of what the Logistics Scheduler agent can do")]
    public Task<string> GetCapabilitiesAsync()
    {
        return Task.FromResult(@"[LOGISTICS SCHEDULER] My specialized capabilities:

📅 CALENDAR MANAGEMENT (Microsoft Outlook):
  - Create event calendar entries
  - Schedule venue bookings
  - Coordinate catering deliveries
  - Schedule A/V setup
  - Update event times and locations

📁 LOGISTICS ACCESS (SharePoint - Read Only):
  - Read logistics timelines
  - Access attendee lists
  - Review scheduling documents

💬 TEAM COORDINATION (Microsoft Teams):
  - Post scheduling updates
  - Coordinate with other agents
  - Notify team of changes

I specialize in scheduling and calendar management.
For document creation, ask the Event Coordinator agent.
For vendor emails and budgets, ask the Budget & Finance agent.");
    }

    [KernelFunction("create_event_timeline"), Description("Create a comprehensive timeline of all scheduled activities for an event")]
    public Task<string> CreateEventTimelineAsync(
        [Description("Event name")] string eventName,
        [Description("Event date (MM/DD/YYYY)")] string eventDate)
    {
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Event timeline created for: {eventName}

DATE: {eventDate}

MORNING SCHEDULE:
7:00 AM - Venue Access Opens
7:30 AM - A/V Equipment Setup Begins
8:00 AM - Catering Delivery & Setup
8:30 AM - Final Venue Walkthrough
8:45 AM - Registration Desk Opens

EVENT SCHEDULE:
9:00 AM - Welcome & Opening Remarks
9:30 AM - Keynote Presentation
10:30 AM - Morning Break
11:00 AM - Breakout Sessions
12:00 PM - Lunch

AFTERNOON SCHEDULE:
1:00 PM - Panel Discussion
2:00 PM - Afternoon Break
2:30 PM - Workshop Sessions
4:00 PM - Closing Remarks
4:30 PM - Networking Reception

EVENING:
5:00 PM - Event Concludes
5:30 PM - Venue Cleanup Begins
6:30 PM - Venue Access Ends

Total Duration: 11.5 hours
Status: All activities scheduled in Outlook Calendar");
    }

    [KernelFunction("send_calendar_invitations"), Description("Send calendar invitations to all event attendees")]
    public Task<string> SendCalendarInvitationsAsync(
        [Description("Event name")] string eventName,
        [Description("Number of attendees to invite")] int attendeeCount,
        [Description("Event date and time")] string eventDateTime,
        [Description("Location")] string location)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        
        return Task.FromResult($@"[LOGISTICS SCHEDULER] ✓ Calendar invitations sent

Event: {eventName}
Date/Time: {eventDateTime}
Location: {location}
Invitations Sent: {attendeeCount}
Sent: {timestamp}

Invitation Details:
✓ Calendar appointment with event details
✓ Location and directions included
✓ Reminder set for 1 day before event
✓ Option to add to personal calendar
✓ RSVP tracking enabled

Delivery Status: All invitations delivered successfully
Expected Response Rate: 85-90% within 48 hours

Attendees will receive:
- Email notification with .ics calendar file
- Automatic addition to Outlook calendar
- Mobile calendar sync");
    }

    #endregion
}
