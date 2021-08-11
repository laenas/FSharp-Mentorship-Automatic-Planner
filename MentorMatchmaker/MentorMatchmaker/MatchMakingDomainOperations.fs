﻿module MentorMatchmaker.DomainOperations

open System.Linq

open FSharpPlus.Data
open FSharpPlus.Operators

open Infra
open Utilities
open DomainTypes

let (|LessThan|_|) i l =
    if List.length l < i then Some l else None

let availabilityToOverlap (minimumOverlap:int) menteeAvailability mentorAvailability =
    let overlap = 
        List.intersect menteeAvailability.UtcHours mentorAvailability.UtcHours
        |> List.toConsecutivePairs
        |> List.filter(fun (previousHour, currentHour) -> previousHour.Hours + 1 = currentHour.Hours)
        |> List.map(fun (previousHour, currentHour) -> { UtcStartTime = previousHour; UtcEndTime = currentHour })

    match mentorAvailability.Weekday = menteeAvailability.Weekday,overlap with
    | false, _
    | true, LessThan minimumOverlap _ -> None
    | _ -> Some { Weekday = mentorAvailability.Weekday; MatchedAvailablePeriods = NonEmptyList.ofList overlap }

let scheduleToOverlap menteeSchedule mentorSchedule minimumOverlap =    
    (menteeSchedule.AvailableDays,mentorSchedule.AvailableDays)
    ||> NonEmptyList.map2Shortest (availabilityToOverlap minimumOverlap)
    |> NonEmptyList.toList
    |> List.choose id

let extractMatchingFsharpTopics (mentorFsharpTopics: FsharpTopic nel) (menteeFsharpTopics: FsharpTopic nel) =
    NonEmptyList.intersect mentorFsharpTopics menteeFsharpTopics

let tryMatch (minimumOverlap: int) (mentor: Mentor) (mentee: Mentee) =
    let overlaps = (mentee.MenteeInformation.MentorshipSchedule, mentor.MentorInformation.MentorshipSchedule, minimumOverlap) |||> scheduleToOverlap
    let matchingInterests = (mentor.AreasOfExpertise, mentee.TopicsOfInterest) ||> extractMatchingFsharpTopics
    
    match overlaps,matchingInterests with
    | [], _
    | _, [] -> None
    | _ -> Some { Mentor = mentor; Mentee = mentee; MatchingFsharpInterests = matchingInterests; Overlaps = overlaps }

let findPotentialMentees (mentees: Mentee list) (minimumOverlap: int) (mentor: Mentor) =
    List.choose (tryMatch minimumOverlap mentor) mentees 

let generateMeetingTimes (mentorSchedule: CalendarSchedule) (menteeSchedule: CalendarSchedule) =
    let filterForSameWeekDay (availableDays1: DayAvailability nel) (availableDays2: DayAvailability nel) =        
        let availabilitiesList1 = availableDays1 |> NonEmptyList.toList
        let availabilitiesList2 = availableDays2 |> NonEmptyList.toList

        availabilitiesList1 |> List.filter(fun day -> availabilitiesList2 |> List.exists(fun x -> x.Weekday = day.Weekday))

    let sortByWeekDayName nonEmptyAvailableDays =
        sort nonEmptyAvailableDays

    let mentorAvailableDaysList, menteeAvailableDaysList =
        if mentorSchedule.AvailableDays.Length = menteeSchedule.AvailableDays.Length then
            sortByWeekDayName mentorSchedule.AvailableDays, sortByWeekDayName menteeSchedule.AvailableDays
        else
            let mentorWeekSchedule = (mentorSchedule.AvailableDays, menteeSchedule.AvailableDays) ||> filterForSameWeekDay
            let menteeWeekSchedule = (menteeSchedule.AvailableDays, mentorSchedule.AvailableDays) ||> filterForSameWeekDay
            sortByWeekDayName (NonEmptyList.create mentorWeekSchedule.Head mentorWeekSchedule.Tail), sortByWeekDayName (NonEmptyList.create menteeWeekSchedule.Head menteeWeekSchedule.Tail)

    (mentorAvailableDaysList, menteeAvailableDaysList)
    ||> NonEmptyList.map2Shortest (fun mentorAvailabilitiesOfTheDay menteeAvailabilitiesOfTheDay -> availabilityToOverlap mentorAvailabilitiesOfTheDay menteeAvailabilitiesOfTheDay)
    |> choose id

let findAllPotentialMentorshipMatches minimumOverlap mentors mentees =
    mentors 
    |> List.collect (findPotentialMentees mentees minimumOverlap)
    |> List.map(fun mentorshipMatch -> 
        let orderedInterestBasedOnPopularity = mentorshipMatch.MatchingFsharpInterests |> List.sortByDescending(fun fsharpTopic -> fsharpTopic.PopularityWeight)
        { mentorshipMatch with MatchingFsharpInterests = orderedInterestBasedOnPopularity }
    )

type CollectingMentorshipPairing = {
    Matches: Map<Mentor, ConfirmedMentorshipApplication list>
    MatchedMentees: Set<Mentee>
    MatchedMentors: Set<Mentor>
    RemainingPotentialMatches: PotentialMentorshipMatch list
}

let rec createUniqueMentorshipMatches (collectingPairing: CollectingMentorshipPairing)  =
    let prepareDataForMatching (potentialMatch: PotentialMentorshipMatch) (confirmedMatches: ConfirmedMentorshipApplication list) (potentialMatchesRemaining: PotentialMentorshipMatch list) =
        let mentee = potentialMatch.Mentee
        let mentor = potentialMatch.Mentor
        let sessionHours = generateMeetingTimes mentee.MenteeInformation.MentorshipSchedule mentor.MentorInformation.MentorshipSchedule
        if Seq.length sessionHours > 0 then
            let confirmedMentoshipMatch = {
                MatchedMentee = mentee
                MatchedMentor = mentor
                FsharpTopic = potentialMatch.MatchingFsharpInterests.Head
                CouldMentorHandleMoreWork = confirmedMatches.Length + 1 = (mentor.SimultaneousMenteeCount |> int)
                MeetingTimes = NonEmptyList.ofSeq sessionHours 
            }
    
            let updatedCollectionPairing = {
                Matches = collectingPairing.Matches |> Map.add mentor (confirmedMentoshipMatch :: confirmedMatches)
                MatchedMentees =  collectingPairing.MatchedMentees |> Set.add mentee
                MatchedMentors = collectingPairing.MatchedMentors |> Set.add mentor 
                RemainingPotentialMatches = potentialMatchesRemaining |> List.filter(fun x -> x <> potentialMatch)
            }
    
            updatedCollectionPairing
    
        else
            { collectingPairing with RemainingPotentialMatches = potentialMatchesRemaining.Tail }
    
    match collectingPairing.RemainingPotentialMatches with
    | [] -> collectingPairing

    | potentialMatch :: remainingPotentialMatches ->
        if collectingPairing.MatchedMentees.Contains potentialMatch.Mentee || collectingPairing.MatchedMentors.Contains potentialMatch.Mentor then
            createUniqueMentorshipMatches { collectingPairing with RemainingPotentialMatches = remainingPotentialMatches }
        else
            let optMentorWithMatches = collectingPairing.Matches |> Map.tryFind potentialMatch.Mentor
            match optMentorWithMatches with
            | Some confirmedMatches when confirmedMatches.Length >= (potentialMatch.Mentor.SimultaneousMenteeCount |> int) ->
                createUniqueMentorshipMatches collectingPairing
            
            | Some confirmedMatches ->
                (potentialMatch, confirmedMatches, collectingPairing.RemainingPotentialMatches) 
                |||> prepareDataForMatching 
                |> createUniqueMentorshipMatches

            | None ->
                (potentialMatch, [], collectingPairing.RemainingPotentialMatches) 
                |||> prepareDataForMatching
                |> createUniqueMentorshipMatches

let getConfirmedMatchesFromPlanner (plannerInputs: MentorshipPlannerInputs) =
    let collectingPairings = 
        {
            Matches = Map.empty
            MatchedMentees = plannerInputs.MatchedMenteesSet
            MatchedMentors = plannerInputs.MatchedMentorSet
            RemainingPotentialMatches = (plannerInputs.FullMentorList, plannerInputs.FullMenteeList, plannerInputs.NumberOfHoursRequiredForOverlap) |||> findAllPotentialMentorshipMatches
        }        
        |> createUniqueMentorshipMatches
    
    let mentorshipPairings =
        collectingPairings.Matches
        |> Map.map(fun _ confirmedMatches -> confirmedMatches)
        |> Map.toList
        |> List.map(fun (_, confirmedMatches) -> confirmedMatches)
        |> List.concat

    (mentorshipPairings, collectingPairings.MatchedMentees, collectingPairings.MatchedMentors)

[<RequireQualifiedAccess>]
module Matchmaking =
    open System.IO
    open System.Linq

    let rec getMentorshipPairing (plannerInputs: MentorshipPlannerInputs) =
        let filterToUnmatchedMentees (unmatchedMentees: Mentee list) (matchedMentees: Set<Mentee>) =
            unmatchedMentees |> List.filter(fun mentee -> matchedMentees.Contains mentee <> true)

        let filterToUnmatchedMentors (unmatchedMentors: Mentor list) (confirmedPairings: ConfirmedMentorshipApplication list) =
            unmatchedMentors |> List.filter(fun unmatchedMentor -> confirmedPairings.Any(fun x -> x.MatchedMentor = unmatchedMentor) <> true)

        match (plannerInputs.FullMentorList, plannerInputs.NumberOfHoursRequiredForOverlap) with
        | ([], _) ->
            plannerInputs.ConfirmedMatches, plannerInputs

        | (_, 0) ->
            plannerInputs.ConfirmedMatches, plannerInputs

        | _ ->
            let (confirmedMatches, matchedMenteeSet, matchedMentorsSet) = getConfirmedMatchesFromPlanner plannerInputs
            let updatedPlanner = 
                { plannerInputs with
                    FullMenteeList = filterToUnmatchedMentees plannerInputs.FullMenteeList plannerInputs.MatchedMenteesSet
                    FullMentorList = filterToUnmatchedMentors plannerInputs.FullMentorList plannerInputs.ConfirmedMatches
                    ConfirmedMatches = plannerInputs.ConfirmedMatches @ confirmedMatches
                    MatchedMenteesSet = matchedMenteeSet
                    MatchedMentorSet = matchedMentorsSet
                    NumberOfHoursRequiredForOverlap = plannerInputs.NumberOfHoursRequiredForOverlap - 1 }

            getMentorshipPairing updatedPlanner

    // TODO: Could be fun to imrpove the backend and domain model by discerning between a unmatched and matched applicant. Plus removing this bool would make me feel a lot better.
    type UnmatchedApplicant = {
        Name: string
        EmailAddress: string
        IsMentor: bool
        Interests: FsharpTopic nel
        AvailableDays: DayAvailability nel
    }

    let dumpToFileUnmatchedApplicants (plannerInputs: MentorshipPlannerInputs) =
        let dumpToFileApplicationData (unmatchedApplicant: UnmatchedApplicant) =
            let topics = unmatchedApplicant.Interests |> NonEmptyList.map(fun topic -> topic.Name) |> String.concat " ,"
            let availabilities = 
                unmatchedApplicant.AvailableDays
                |> NonEmptyList.map(fun day ->
                    let availableHours = day.UtcHours |> List.map(fun utc -> $"{utc.Hours}") |> String.concat " ,"
                    $"{day.Weekday}: {availableHours}"
                )
                |> String.concat  ", "

            let applicantType = if unmatchedApplicant.IsMentor then "Mentor" else "Mentee"
            $"
                {applicantType}: Name -> {unmatchedApplicant.Name} Email -> {unmatchedApplicant.EmailAddress}
                Topics: {topics}
                Available hours in UTC: 
                        {availabilities}
            "
        let transformedMenteesInUnmatchedApplicants = 
            plannerInputs.FullMenteeList
            |> List.filter(fun mentee -> plannerInputs.MatchedMenteesSet.Contains mentee <> true)
            |> List.map(fun x -> 
                { 
                    Name = x.MenteeInformation.Fullname
                    EmailAddress = x.MenteeInformation.EmailAddress
                    IsMentor = false
                    Interests = x.TopicsOfInterest
                    AvailableDays = x.MenteeInformation.MentorshipSchedule.AvailableDays
                }
        )

        let transformedMentorsInUnmatchedApplicants = 
            plannerInputs.FullMentorList
            |> List.filter(fun mentor -> plannerInputs.MatchedMentorSet.Contains mentor <> true)
            |> List.map(fun x -> 
                { 
                    Name = x.MentorInformation.Fullname
                    EmailAddress = x.MentorInformation.EmailAddress
                    IsMentor = true
                    Interests = x.AreasOfExpertise
                    AvailableDays = x.MentorInformation.MentorshipSchedule.AvailableDays
                }
        )

        let unmatchedApplicants = transformedMenteesInUnmatchedApplicants @ transformedMentorsInUnmatchedApplicants

        let fileContent = 
            unmatchedApplicants 
            |> List.map(fun application -> $"{dumpToFileApplicationData application}")
            |> String.concat("\n")

        System.IO.File.WriteAllText("applicationDataDump.txt", fileContent)

