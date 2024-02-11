namespace HanselminutesBot.Shared

open System.Xml
open System.ServiceModel.Syndication

module PodcastSource =
    let private feedUrl = "https://hanselminutes.com/subscribearchives"

    let GetFeed () =
        let reader = XmlReader.Create(feedUrl)
        let feed = SyndicationFeed.Load(reader)
        reader.Close()
        feed