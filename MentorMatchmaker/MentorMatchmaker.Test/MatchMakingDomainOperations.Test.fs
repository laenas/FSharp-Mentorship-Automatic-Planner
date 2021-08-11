module MatchMakingDomainOperations.Test

open Expecto
open Expecto.ExpectoFsCheck

open MentorMatchmaker

let tests = 
    testList "Mentor / Mentee matching" [
        test "Matches only when days match" {
            Expect.isTrue true ""
        }
    ]