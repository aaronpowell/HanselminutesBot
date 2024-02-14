# .NET Aspire + Azure OpenAI demo app

Currently a WIP, best to ask me for how to run it locally, but here's the general idea:

- Create an Azure OpenAI resource with a `gpt-35-turbo` model and a `text-embedding-ada-002` model
- Create an Azure AI Speech resource (optional - disable by setting `Speech:Enabled` to `false` in `appsettings.json`)
- Create an `appsettings.Development.json` file in the AppHost project and populate the details from Azure Resources as per the empty ones in the `appsettings.json`
- Launch the app, navigate to the frontend -> management screen
- Wait for podcasts to load, choose the one(s) to index and click the **Index Selected Items** button
- Wait for a bunch of time (no UI progress, but you can click the "Retrieve Status" button to see the indexing status)
- Once indexing is done, go to the frontend `/` index, enter a prompt and see what happens
